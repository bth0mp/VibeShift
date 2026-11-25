# Live checks of what unit tests can't reach: the tray menu, the quit dialog, the command line, "Ask me" and
# starting up in gaming mode. Run after `dotnet build VibeShift.sln -c Release`.
#
# Safe to run next to your own VibeShift: every copy started here gets its own data folder (VIBESHIFT_DATA), which
# also gives it its own single-instance lock, and every close toggle in that folder is off, so nothing of yours is
# closed. The only app opened and closed is Windows' Character Map. Tray menus and the quit dialog do flash on
# screen for a moment, so it refuses to run while a game is running.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe = Join-Path $repo 'src\VibeShift\bin\Release\net10.0-windows\VibeShift.exe'
if (-not (Test-Path $exe)) { throw "Build first: $exe is missing" }
& dotnet fsi (Join-Path $PSScriptRoot 'game-now.fsx')
if ($LASTEXITCODE -ne 0) { throw 'A game is running: not popping test windows over it' }
if (Get-Process charmap -ErrorAction SilentlyContinue) { throw 'Character Map is already open: close it first (the test opens and closes it)' }
$charmap = Join-Path $env:WINDIR 'System32\charmap.exe'
# Every built-in row is switched off below. A row added to the catalog and not to this list would be closed for real.
$builtInIds = 'claude-desktop,chatgpt,cursor,windsurf,copilot,m365copilot,ollama,lmstudio,mcp,vscode,docker-wsl,cli-agents,browsers,wallpaper-engine,powertoys,widgets'.Split(',')
$catalogIds = [regex]::Matches((Get-Content (Join-Path $repo 'src\VibeShift.Core\Catalog\Catalog.cs') -Raw), 'new\("([a-z0-9-]+)",') | ForEach-Object { $_.Groups[1].Value }
$unknown = @($catalogIds | Where-Object { $builtInIds -notcontains $_ })
if ($catalogIds.Count -eq 0 -or $unknown.Count -gt 0) { throw "Catalog rows this script doesn't switch off: $($unknown -join ', ') (add them to `$builtInIds)" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class TrayPoke {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    // WinForms NotifyIcon's callback message (WM_USER + 1024) with WM_RBUTTONUP opens THAT process's tray menu:
    // no mouse, no hunting for the icon, and never another copy's menu.
    public static int RightClick(int pid) {
        var hits = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (p == pid && c.ToString().StartsWith("WindowsForms10.Window.0.")) hits.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var h in hits) PostMessage(h, 0x0400 + 1024, IntPtr.Zero, (IntPtr)0x0205);
        return hits.Count;
    }
}
'@
$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]
# An element of the test copy by name: its top-level windows first (fast), then inside them. Never another process.
function Find($procId, $name, $seconds = 10) {
    $mine = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $procId)
    $named = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
    foreach ($i in 1..($seconds * 10)) {
        foreach ($top in $ae::RootElement.FindAll($scope::Children, $mine)) {
            if ($top.Current.Name -eq $name) { return $top }
            $hit = $top.FindFirst($scope::Descendants, $named)
            if ($hit) { return $hit }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "'$name' not found in process $procId"
}
function Click($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function TrayMenu($proc, $item) {
    if ([TrayPoke]::RightClick($proc.Id) -eq 0) { throw 'no tray window found' }
    Find $proc.Id $item
}
function Wait-Until($seconds, [scriptblock]$condition) {
    foreach ($i in 1..($seconds * 4)) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    $false
}

$dir = Join-Path $env:TEMP ('vibeshift-live-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$oldData = $env:VIBESHIFT_DATA
$env:VIBESHIFT_DATA = $dir
$session = Join-Path $dir 'session.json'
$log = Join-Path $dir 'vibeshift.log'
$failed = New-Object System.Collections.Generic.List[string]
function Check($name, $ok) { if ($ok) { "  ok    $name" } else { "  FAIL  $name"; $failed.Add($name) } }
function Logged($text) { (Test-Path $log) -and [bool](Select-String -Path $log -SimpleMatch $text -Quiet) }

function Write-Settings($extra = @{}) {
    $toggles = [ordered]@{}; foreach ($i in $builtInIds) { $toggles[$i] = $false }
    $s = [ordered]@{ SchemaVersion = 2; Theme = 'Dark'; Toggles = $toggles; VibeApps = @(); GameApps = @(); MyGames = @()
                     AutoMode = $false; AskOnGameStart = $false; ReturnWhenGameCloses = $false; Hotkeys = $false; StartWithWindows = $false }
    foreach ($k in $extra.Keys) { $s[$k] = $extra[$k] }
    $s | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'settings.json') -Encoding UTF8
}
function Write-Session($startedUtc, [switch]$Automatic) {
    [ordered]@{ StartedUtc = $startedUtc.ToString('o'); Automatic = [bool]$Automatic
                Closed = @([ordered]@{ EntryId = 'test-charmap'; Name = 'Character Map'; ExePath = $charmap; Aumid = $null })
                ClosedEntryIds = @('test-charmap') } | ConvertTo-Json -Depth 5 | Set-Content $session -Encoding UTF8
}
function Start-Copy([string[]]$arguments = @('--tray')) {
    $p = Start-Process $exe -ArgumentList $arguments -PassThru
    $null = $p.Handle
    Start-Sleep 6
    if ($p.HasExited) { throw 'the test copy exited on start' }
    if (Test-Path (Join-Path $dir 'settings.json.bad')) { Stop-Copy $p; throw 'the test settings were rejected, so defaults (with real apps on) applied' }
    $p
}
function Stop-Copy($p) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(5000) | Out-Null } }
function Close-Charmap { Get-Process charmap -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
function Reset { Remove-Item $session, $log -ErrorAction SilentlyContinue }

$vs = $null
try {
    'Quit from the tray'
    Write-Settings
    $vs = Start-Copy
    Click (TrayMenu $vs 'Quit')
    Check 'vibe coding mode: Quit exits with no dialog' ($vs.WaitForExit(10000))

    Write-Session ([DateTime]::UtcNow)
    $vs = Start-Copy
    Click (TrayMenu $vs 'Quit')
    $dialog = Find $vs.Id 'Quit VibeShift'
    $body = $dialog.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like "You're in gaming mode*" }
    Check 'gaming mode: the dialog names the closed app' ($body -like '*Character Map is still closed*')
    Click (Find $vs.Id 'Cancel')
    Start-Sleep 2
    Check 'Cancel: still running, session kept' (-not $vs.HasExited -and (Test-Path $session))
    Click (TrayMenu $vs 'Quit')
    Click (Find $vs.Id 'Reopen and quit')
    $exited = $vs.WaitForExit(20000)
    # Started from a terminal that sits in a job (an AI app's), the copy reopens apps through Explorer, which starts
    # them a moment after VibeShift has gone: wait for it.
    Check 'Reopen and quit: Character Map reopened, session cleared, exited' ($exited -and (Wait-Until 10 { Get-Process charmap -ErrorAction SilentlyContinue }) -and -not (Test-Path $session))
    Close-Charmap
    Write-Session ([DateTime]::UtcNow)
    $vs = Start-Copy
    Click (TrayMenu $vs 'Quit')
    Click (Find $vs.Id 'Quit anyway')
    Check 'Quit anyway: exited, session kept, nothing reopened' ($vs.WaitForExit(10000) -and (Test-Path $session) -and -not (Get-Process charmap -ErrorAction SilentlyContinue))
    Check 'both kinds of quit are logged' ((Logged 'Quit in gaming mode: apps left closed') -and [bool](Select-String -Path $log -Pattern '  Quit$' -Quiet))

    'Command line'
    Reset; Write-Settings
    $vs = Start-Copy
    & $exe --gaming; Check '--gaming switches the running copy to gaming mode' (Wait-Until 20 { Test-Path $session })
    & $exe --gaming; Start-Sleep 4; Check '--gaming again changes nothing' (Test-Path $session)
    & $exe --coding; Check '--coding switches back' (Wait-Until 20 { -not (Test-Path $session) })
    & $exe --toggle; Check '--toggle switches to gaming' (Wait-Until 20 { Test-Path $session })
    # session.json is written as a switch starts. A toggle sent before it ends means "the other mode" as things stood,
    # so it wouldn't undo it: wait for the switch to finish, as someone watching the tray would.
    $switched = { @(Select-String -Path $log -SimpleMatch 'Gaming mode (manual)').Count -ge 2 }
    Check '...and finishes' (Wait-Until 20 $switched); Start-Sleep 1
    & $exe --toggle; Check '--toggle switches back' (Wait-Until 20 { -not (Test-Path $session) })
    Check 'the running copy stayed up and logged the commands' (-not $vs.HasExited -and (Logged 'Command line: --gaming') -and (Logged 'Command line: --toggle'))
    Stop-Copy $vs
    Reset
    $vs = Start-Process $exe -ArgumentList '--gaming' -PassThru; $null = $vs.Handle
    Check 'a fresh start with --gaming ends up in gaming mode' (Wait-Until 30 { Test-Path $session })
    Check '...and stays running, in the tray' (-not $vs.HasExited -and $vs.MainWindowHandle -eq 0)
    Stop-Copy $vs

    'Tray: When a game starts'
    Reset; Write-Settings
    $vs = Start-Copy
    $menu = TrayMenu $vs 'When a game starts'
    $expand = $null
    if ($menu.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { $expand.Expand() } else { Click $menu }
    Click (Find $vs.Id 'Ask me')
    $saved = { Get-Content (Join-Path $dir 'settings.json') -Raw | ConvertFrom-Json }
    Check 'choosing Ask me saves it' (Wait-Until 5 { (& $saved).AskOnGameStart -eq $true -and (& $saved).AutoMode -eq $false })
    Stop-Copy $vs

    'Ask me'
    Reset; Write-Settings @{ AskOnGameStart = $true; MyGames = @($charmap) }
    $vs = Start-Copy
    Start-Process $charmap
    Check 'a game starting is announced once' (Wait-Until 20 { Logged 'Ask me: charmap started' })
    Start-Sleep 15
    Check '...and nothing switches by itself' (-not (Test-Path $session) -and @(Select-String -Path $log -SimpleMatch 'Ask me:').Count -eq 1)
    Close-Charmap
    Stop-Copy $vs

    'Starting up in gaming mode'
    Reset; Write-Settings @{ ReturnWhenGameCloses = $true }
    Write-Session ([DateTime]::UtcNow.AddMinutes(-1))
    $vs = Start-Copy
    Start-Sleep 20
    Check 'a session from this sign-in waits for a game (stays in gaming mode)' ((Test-Path $session) -and (Logged 'Started in gaming mode') -and -not (Logged 'from before the last sign-out'))
    Stop-Copy $vs
    Reset; Write-Session ([DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
    $vs = Start-Copy
    Check 'a session from before the last sign-out switches back by itself' (Wait-Until 45 { -not (Test-Path $session) })
    Check '...reopening what it closed, and saying why' ((Wait-Until 10 { Get-Process charmap -ErrorAction SilentlyContinue }) -and (Logged 'from before the last sign-out'))
    Stop-Copy $vs
    Close-Charmap
    Reset; Write-Session ([DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
    $vs = Start-Copy @('--gaming')
    Start-Sleep 20
    Check '...but started with --gaming, it stays in gaming mode and waits for a game, without the 2-hour reminder' ((Test-Path $session) -and (Logged 'now waiting for a game, as asked') -and -not (Logged 'Reminded') -and -not (Get-Process charmap -ErrorAction SilentlyContinue))
    Stop-Copy $vs
    # A session Auto mode started switches back whatever "Switch back when my game closes" says, also after --gaming.
    Reset; Write-Settings @{ MyGames = @($charmap) }
    Write-Session ([DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)) -Automatic
    $vs = Start-Copy @('--gaming')
    Start-Process $charmap
    Check 'an Auto mode session, with the setting off: --gaming waits for the game' ((Wait-Until 20 { Logged 'Game running: charmap' }) -and (Test-Path $session))
    Close-Charmap
    Check '...and still switches back once it closes, reopening what it closed' ((Wait-Until 40 { -not (Test-Path $session) }) -and (Wait-Until 10 { Get-Process charmap -ErrorAction SilentlyContinue }))
    Stop-Copy $vs
    Close-Charmap
}
finally {
    Stop-Copy $vs
    Start-Sleep 2 # a Character Map that Explorer is still starting for a copy that has just gone
    Close-Charmap
    if ($failed.Count -gt 0 -and (Test-Path $log)) { ''; 'log of the last test:'; Get-Content $log }
    Start-Sleep 1
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    $env:VIBESHIFT_DATA = $oldData
}
''
if ($failed.Count -gt 0) { throw "$($failed.Count) live check(s) failed: $($failed -join '; ')" }
'All live checks passed.'
