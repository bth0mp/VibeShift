using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using VibeShift.Core;
using VibeShift.Core.Catalog;
using VibeShift.Core.Games;
using VibeShift.Core.Processes;
using VibeShift.Core.State;
using VibeShift.Core.Switching;
using VibeShift.Core.Updates;

namespace VibeShift;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly JsonStore store;
    private readonly Switcher switcher;
    private readonly Dictionary<string, AppRow> rows = [];
    private readonly Dictionary<string, VibeAppRow> vibeRows = [];
    private bool vibeBusy;
    private readonly HashSet<string> failedReopen = [];
    private readonly bool demo;
    private SessionState? session;
    private Mode mode;
    private bool isBusy;
    private string freeRamText = "", appsText = "", thirdValue = "", busyText = "", appsMessage = "";

    public MainViewModel(JsonStore store, Settings settings, bool demo = false)
    {
        this.store = store;
        this.demo = demo;
        Settings = settings;
        switcher = new Switcher(store);
        session = demo ? null : store.LoadSession();
        mode = session is null ? Mode.Coding : Mode.Gaming;
        PrimaryCommand = new RelayCommand(() => ToggleModeAsync(automatic: false));
        OpenAllCommand = new RelayCommand(OpenAllAsync);
        CloseAllCommand = new RelayCommand(CloseAllAsync);
        PlayCommand = new RelayCommand(PlayAsync);
        CheckUpdatesCommand = new RelayCommand(() => CheckUpdatesAsync(quiet: false));
        UpdateCommand = new RelayCommand(UpdateAsync);
        MyGames = new ObservableCollection<string>(settings.MyGames);
        if (!demo) stats = store.LoadStats();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every completed switch with the new mode and whether Auto mode did it.</summary>
    public event Action<Mode, bool>? Switched;

    public Settings Settings { get; }
    public ICommand PrimaryCommand { get; }

    /// <summary>Game PIDs seen by Auto mode; kept out of reach of the switch.</summary>
    public IReadOnlyCollection<int> CurrentGamePids { get; set; } = [];

    /// <summary>Finds running games right now, so every switch (not just Auto mode's) protects the game.</summary>
    public Func<IReadOnlyCollection<int>>? FindGames { get; set; }

    public ObservableCollection<AppRow> HomeRows { get; } = [];
    public ObservableCollection<AppRow> AllRows { get; } = [];
    public ObservableCollection<string> MyGames { get; }

    // ----- Play button -----

    /// <summary>Installed games (Steam, Epic, My games), most recently played first.</summary>
    public IReadOnlyList<InstalledGame> Games { get; private set; } = [];

    public ICommand PlayCommand { get; }

    /// <summary>The Play button's game: the one picked in Settings, else the most recently played.</summary>
    public InstalledGame? PlayTarget => Games.FirstOrDefault(g => g.Id == Settings.PlayGame) ?? Games.FirstOrDefault();

    public bool CanPlay => PlayTarget is not null && IsCoding;
    public string PlayText => PlayTarget is { } g ? "▶  Play " + g.Name : "";

    /// <summary>Settings page selection; null clears it back to "most recently played".</summary>
    public string? PlayGameId
    {
        get => PlayTarget?.Id;
        set
        {
            if (value is null || value == PlayTarget?.Id) return;
            Settings.PlayGame = value;
            SaveSettings();
            RaisePlay();
        }
    }

    public async Task LoadGamesAsync()
    {
        if (demo) return;
        var mine = Settings.MyGames.ToList();
        Games = await Task.Run(() => GameLibraries.InstalledGames(mine));
        Raise(nameof(Games));
        RaisePlay();
    }

    /// <summary>One click from coding to in-game: switch to gaming mode (freeing the RAM first), then launch the game.</summary>
    public async Task PlayAsync()
    {
        if (PlayTarget is not { } game || demo) return;
        // A Vibe page action or a switch may still be finishing: wait for it (up to 20 s) rather than launch the
        // game without freeing the RAM first.
        for (int i = 0; i < 100 && (IsBusy || vibeBusy); i++) await Task.Delay(200);
        if (Mode == Mode.Coding) await ToggleModeAsync(automatic: false);
        if (Mode == Mode.Coding) Log.Write("Play: couldn't switch to gaming mode first; launching anyway");
        try
        {
            System.Diagnostics.Process.Start(game.ToStartInfo())?.Dispose();
            Log.Write($"Play: launched {game.Name}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Play: couldn't launch {game.Name}: {e.Message}");
        }
    }

    private void RaisePlay()
    {
        Raise(nameof(PlayTarget));
        Raise(nameof(PlayText));
        Raise(nameof(CanPlay));
        Raise(nameof(PlayGameId));
    }

    // ----- Hotkeys -----

    public bool Hotkeys
    {
        get => Settings.Hotkeys;
        set { Settings.Hotkeys = value; SaveSettings(); Raise(); }
    }

    private string hotkeyStatus = "";

    /// <summary>Set by the App after registering: empty when all three shortcuts work.</summary>
    public string HotkeyStatus
    {
        get => hotkeyStatus;
        set { if (Set(ref hotkeyStatus, value)) Raise(nameof(HasHotkeyStatus)); }
    }

    public bool HasHotkeyStatus => HotkeyStatus.Length > 0;

    // ----- Stats -----

    private List<GamingRecord> stats = [];
    private string statsText = "";

    /// <summary>Home: "Last 7 days: ..." while vibe coding, "In gaming mode for ..." while gaming.</summary>
    public string StatsText
    {
        get => statsText;
        private set { if (Set(ref statsText, value)) Raise(nameof(HasStats)); }
    }

    public bool HasStats => StatsText.Length > 0;

    private void UpdateStats() =>
        StatsText = IsGaming
            ? session is { } s ? "In gaming mode for " + Format.Duration(DateTime.UtcNow - s.StartedUtc) : ""
            : Stats.Week(stats, DateTime.UtcNow);

    // ----- Gaming power plan -----

    public sealed record PowerPlanOption(string Id, string Name);

    /// <summary>"Don't change" plus this PC's own power plans (OEM ones included).</summary>
    public IReadOnlyList<PowerPlanOption> PowerPlanOptions { get; private set; } = [new("", "Don't change")];

    /// <summary>The plan to use while gaming; "" leaves the power plan alone.</summary>
    public string GamingPowerPlan
    {
        get => Settings.GamingPowerPlan ?? "";
        set
        {
            // null is the list letting go of its selection while its items reload, not a choice.
            if (value is null || value == GamingPowerPlan) return;
            Settings.GamingPowerPlan = value.Length == 0 ? null : value;
            SaveSettings();
            Raise();
        }
    }

    public async Task LoadPowerPlansAsync()
    {
        if (demo) return;
        try
        {
            var plans = await Task.Run(PowerPlans.List);
            PowerPlanOptions = [new("", "Don't change"), .. plans.Select(p => new PowerPlanOption(p.Guid, p.Name))];
            Raise(nameof(PowerPlanOptions));
            Raise(nameof(GamingPowerPlan));
        }
        catch (Exception e)
        {
            Log.Write("Couldn't list power plans: " + e.Message);
        }
    }

    // ----- Updates -----

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private string updateStatus = "";
    private bool updating;

    /// <summary>Raised once an update is swapped in: the App restarts into the exe at this path.</summary>
    public event Action<string>? RestartRequested;

    public ICommand CheckUpdatesCommand { get; }
    public ICommand UpdateCommand { get; }

    public Release? AvailableUpdate { get; private set; }
    public bool HasUpdate => AvailableUpdate is not null;
    public bool CanCheckUpdates => !updating;

    /// <summary>The Home page's update link: "Update to 1.5.0", or how the update is going.</summary>
    public string UpdateText => updating && HasUpdate ? UpdateStatus : AvailableUpdate is { } r ? $"Update to {r.Version.ToString(3)}" : "";

    public string UpdateStatus
    {
        get => updateStatus;
        private set { if (Set(ref updateStatus, value)) { Raise(nameof(HasUpdateStatus)); Raise(nameof(UpdateText)); } }
    }

    public bool HasUpdateStatus => UpdateStatus.Length > 0;

    /// <summary>Off by default: nothing leaves the PC unless the user asks.</summary>
    public bool CheckForUpdates
    {
        get => Settings.CheckForUpdates;
        set
        {
            Settings.CheckForUpdates = value;
            SaveSettings();
            Raise();
            if (value) _ = CheckUpdatesAsync(quiet: false);
        }
    }

    /// <summary>Asks GitHub for the latest release. The quiet daily check only speaks up when there's an update.</summary>
    public async Task CheckUpdatesAsync(bool quiet)
    {
        if (demo || updating || (quiet && !Settings.CheckForUpdates)) return;
        SetUpdating(true);
        if (!quiet) UpdateStatus = "Checking...";
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            var latest = await Updater.CheckAsync(Http, timeout.Token);
            AvailableUpdate = latest is not null && Updater.IsNewer(latest, typeof(MainViewModel).Assembly.GetName().Version!) ? latest : null;
            Raise(nameof(AvailableUpdate));
            Raise(nameof(HasUpdate));
            if (AvailableUpdate is { } r) UpdateStatus = $"VibeShift {r.Version.ToString(3)} is out.";
            else if (!quiet) UpdateStatus = "You have the latest version.";
        }
        catch (Exception e)
        {
            Log.Write("Update check failed: " + e.Message);
            if (!quiet) UpdateStatus = "Couldn't reach GitHub. Try again later.";
        }
        finally
        {
            SetUpdating(false);
        }
    }

    /// <summary>Downloads the update, checks its SHA-256, swaps it in and has the App restart into it.</summary>
    public async Task UpdateAsync()
    {
        if (AvailableUpdate is not { } r || updating || IsBusy || demo || Environment.ProcessPath is not { } exe) return;
        SetUpdating(true);
        UpdateStatus = $"Downloading {r.Version.ToString(3)}...";
        try
        {
            await Updater.InstallAsync(Http, r, exe);
            // A switch or Vibe page action may have started during the download: restarting now would cut it in half
            // (services stopped but not recorded). The new exe is already in place, so just restart once it's done.
            UpdateStatus = "Restarting after the switch...";
            while (IsBusy || vibeBusy) await Task.Delay(200);
            Log.Write($"Updated to {r.Version.ToString(3)}, restarting");
            UpdateStatus = "Restarting...";
            RestartRequested?.Invoke(exe);
        }
        catch (Exception e)
        {
            Log.Write("Update failed: " + e.Message);
            UpdateStatus = e is InvalidDataException ? e.Message : "Couldn't update this copy. Download the new version from GitHub instead.";
        }
        finally
        {
            SetUpdating(false);
        }
    }

    private void SetUpdating(bool on)
    {
        updating = on;
        Raise(nameof(CanCheckUpdates));
        Raise(nameof(UpdateText));
    }

    // ----- Vibe page: live on/off for AI apps -----

    public ObservableCollection<VibeAppRow> VibeRows { get; } = [];
    public bool HasVibeRows => VibeRows.Count > 0;
    public ICommand OpenAllCommand { get; }
    public ICommand CloseAllCommand { get; }

    /// <summary>Open all / Close all are available (not mid-action, not mid-switch).</summary>
    public bool CanVibeAct => !vibeBusy && !IsBusy;

    public async Task OpenAllAsync()
    {
        await RefreshAsync();
        await RunVibeAsync(VibeRows.Where(r => !r.IsOpen && !r.IsBusy).ToList(), open: true);
    }

    public async Task CloseAllAsync()
    {
        await RefreshAsync();
        await RunVibeAsync(VibeRows.Where(r => r.IsOpen && !r.IsBusy).ToList(), open: false);
    }

    /// <summary>For the tray's AI apps menu: the apps and whether each is running, checked right now.</summary>
    public IReadOnlyList<(string Id, string Name, bool Running)> VibeAppsNow()
    {
        var entries = Switcher.Entries(Settings);
        var detected = SwitchPlanner.Detect(ProcessSnapshot.Take(), entries, ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
            p => p.SessionId != MySessionId);
        var running = detected.Where(d => d.Roots.Count > 0).Select(d => d.Entry.Id).ToHashSet();
        var launchable = Switcher.LaunchTargets(Settings);
        return entries.Where(e => AppCatalog.IsAiApp(e) && (running.Contains(e.Id) || launchable.ContainsKey(e.Id)))
            .Select(e => (e.Id, e.Name, running.Contains(e.Id)))
            .ToList();
    }

    public async Task SetVibeAppAsync(string id, bool open)
    {
        await RefreshAsync();
        if (VibeRows.FirstOrDefault(r => r.Id == id) is { IsBusy: false } row) await RunVibeAsync([row], open);
    }

    private async void OnVibeSwitch(VibeAppRow row, bool open)
    {
        if (vibeBusy || IsBusy)
        {
            row.SetIsOpenSilently(!open); // one thing at a time
            return;
        }
        await RunVibeAsync([row], open);
    }

    private async Task RunVibeAsync(IReadOnlyList<VibeAppRow> targets, bool open)
    {
        if (targets.Count == 0 || vibeBusy || IsBusy || demo) return;
        SetVibeBusy(true);
        foreach (var r in targets)
        {
            r.IsBusy = true;
            r.Status = open ? "opening..." : "closing...";
            r.SetIsOpenSilently(open);
        }
        try
        {
            var ids = targets.Select(r => r.Id).ToList();
            if (open)
            {
                var result = await switcher.OpenAppsAsync(Settings.Clone(), ids);
                foreach (var r in targets.Where(r => result.Failed.Contains(r.Name)))
                {
                    r.Status = "couldn't open";
                    r.SetIsOpenSilently(false);
                }
            }
            else
            {
                // Scan now: with Auto mode off, nothing has looked for games while coding.
                var games = FindGames is { } find ? await Task.Run(find) : CurrentGamePids;
                await switcher.CloseAppsAsync(Settings.Clone(), ids, games);
            }
        }
        catch (Exception e)
        {
            Log.Write("Vibe page action failed: " + e.Message);
        }
        finally
        {
            foreach (var r in targets) r.IsBusy = false;
            SetVibeBusy(false);
        }
        await RefreshAsync();
    }

    private void SetVibeBusy(bool busy)
    {
        vibeBusy = busy;
        Raise(nameof(CanVibeAct));
    }

    private void ApplyVibe(IReadOnlyList<CatalogEntry> entries, IReadOnlyList<Detected> detected)
    {
        var running = detected.Where(d => d.Roots.Count > 0).ToDictionary(d => d.Entry.Id);
        var launchable = Switcher.LaunchTargets(Settings);
        var wanted = new List<VibeAppRow>();
        foreach (var e in entries.Where(AppCatalog.IsAiApp))
        {
            bool isRunning = running.TryGetValue(e.Id, out var d);
            if (!isRunning && !launchable.ContainsKey(e.Id)) continue; // can't be opened until seen running once
            if (!vibeRows.TryGetValue(e.Id, out var row)) vibeRows[e.Id] = row = new VibeAppRow(e, OnVibeSwitch);
            if (!row.IsBusy)
            {
                row.SetIsOpenSilently(isRunning);
                row.Status = isRunning ? "running · " + Format.Bytes(d!.Bytes)
                    : row.Status == "couldn't open" ? row.Status : "closed";
            }
            wanted.Add(row);
        }
        Sync(VibeRows, wanted);
        Raise(nameof(HasVibeRows));
    }

    /// <summary>Apps page: other apps using the most RAM right now, one click from the close list.</summary>
    public ObservableCollection<SuggestionRow> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Game library folders (from the App's game watcher), so games are never suggested.</summary>
    public IReadOnlyList<string> GameRoots { get; set; } = [];

    /// <summary>Raised after any settings save (the game watcher rebuilds on it).</summary>
    public event Action? SettingsChanged;

    // ----- Settings page -----

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        [new(ThemeChoice.System, "Follow Windows"), new(ThemeChoice.Dark, "Dark"), new(ThemeChoice.Light, "Light")];

    public IReadOnlyList<int> ForceCloseOptions { get; } = [3, 5, 10, 15, 30];

    public ThemeChoice Theme
    {
        get => Settings.Theme;
        set
        {
            if (Settings.Theme == value) return;
            Settings.Theme = value;
            ThemeManager.Apply(value);
            SaveSettings();
            Raise();
        }
    }

    public bool AutoMode
    {
        get => Settings.AutoMode;
        set { Settings.AutoMode = value; SaveSettings(); Raise(); }
    }

    public bool StartWithWindows
    {
        get => Settings.StartWithWindows;
        set
        {
            Settings.StartWithWindows = value;
            if (!demo) StartupRegistration.Set(value);
            SaveSettings();
            Raise();
        }
    }

    public bool ExtraBoost
    {
        get => Settings.ExtraBoost;
        set { Settings.ExtraBoost = value; SaveSettings(); Raise(); }
    }

    public bool ReturnWhenGameCloses
    {
        get => Settings.ReturnWhenGameCloses;
        set { Settings.ReturnWhenGameCloses = value; SaveSettings(); Raise(); }
    }

    /// <summary>A gaming session is in progress (session.json exists).</summary>
    public bool HasSession => session is not null;

    public DateTime? SessionStartedUtc => session?.StartedUtc;

    /// <summary>What the last switch back opened, for the tray's welcome-back notification.</summary>
    public string LastReturnMessage { get; private set; } = "";

    public int ForceCloseSeconds
    {
        get => Settings.ForceCloseSeconds;
        set { Settings.ForceCloseSeconds = value; SaveSettings(); Raise(); }
    }

    public string BoostServicesText => string.Join(", ", Settings.BoostServices);

    public string Version => "VibeShift " + typeof(MainViewModel).Assembly.GetName().Version!.ToString(3);

    /// <summary>Inline note on the Apps page, e.g. why an exe couldn't be added.</summary>
    public string AppsMessage
    {
        get => appsMessage;
        private set { if (Set(ref appsMessage, value)) Raise(nameof(HasAppsMessage)); }
    }

    public bool HasAppsMessage => AppsMessage.Length > 0;

    public void AddApp(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (BlockList.IsBlocked(name, path, -1, Environment.ProcessId, new HashSet<int>())
            || string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            AppsMessage = $"VibeShift won't close {name}: it's part of Windows or VibeShift itself.";
            return;
        }
        if (Settings.CustomApps.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            AppsMessage = $"{name} is already in your list.";
            return;
        }
        Settings.CustomApps.Add(path);
        AppsMessage = "";
        SaveSettings();
        _ = RefreshAsync();
    }

    public void RemoveApp(AppRow row)
    {
        Settings.RemoveCustomApp(row.Id);
        rows.Remove(row.Id);
        SaveSettings();
        _ = RefreshAsync();
    }

    public void AddGame(string path)
    {
        if (Settings.MyGames.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Settings.MyGames.Add(path);
        MyGames.Add(path);
        SaveSettings();
    }

    public void RemoveGame(string path)
    {
        Settings.MyGames.Remove(path);
        MyGames.Remove(path);
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (demo) return;
        store.SaveSettings(Settings);
        SettingsChanged?.Invoke();
    }

    public Mode Mode
    {
        get => mode;
        private set
        {
            if (!Set(ref mode, value)) return;
            foreach (var n in new[] { nameof(IsGaming), nameof(IsCoding), nameof(ModeText), nameof(ThirdLabel), nameof(ListTitle), nameof(PrimaryText), nameof(CanPlay) })
                Raise(n);
        }
    }

    public bool IsGaming => Mode == Mode.Gaming;
    public bool IsCoding => Mode == Mode.Coding;
    public string ModeText => IsGaming ? "Gaming mode" : "Vibe coding mode";
    public string ThirdLabel => IsGaming ? "FREED" : "CAN FREE";
    public string ListTitle => IsGaming ? "CLOSED FOR GAMING" : "CLOSE WHEN GAMING";
    public string PrimaryText => IsBusy ? BusyText : IsGaming ? "Back to vibe coding" : "Start gaming";

    public string FreeRamText { get => freeRamText; private set => Set(ref freeRamText, value); }
    public string AppsText { get => appsText; private set => Set(ref appsText, value); }
    public string ThirdValue { get => thirdValue; private set => Set(ref thirdValue, value); }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value)) { Raise(nameof(IsIdle)); Raise(nameof(PrimaryText)); Raise(nameof(CanVibeAct)); }
        }
    }

    public bool IsIdle => !IsBusy;

    public string BusyText
    {
        get => busyText;
        private set { if (Set(ref busyText, value)) Raise(nameof(PrimaryText)); }
    }

    private static readonly int MySessionId = GetSessionId();

    private static int GetSessionId()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        return me.SessionId;
    }

    /// <summary>True when the leftover session was started by Auto mode (it can resume switching back).</summary>
    public bool SessionIsAutomatic => session?.Automatic == true;

    public async Task RefreshAsync()
    {
        if (demo) return;
        try
        {
            int self = Environment.ProcessId;
            int mySession = MySessionId;
            var snapshot = Settings.Clone(); // the UI may edit Settings while this runs
            var entries = Switcher.Entries(snapshot);
            var roots = GameRoots;
            var (detected, avail, estimated, suggestions) = await Task.Run(() =>
            {
                var all = ProcessSnapshot.Take();
                var none = new HashSet<int>();
                // Same block rules as a real switch, so the numbers shown match what would happen.
                var det = SwitchPlanner.Detect(all, entries, ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
                    p => p.SessionId != mySession || BlockList.IsBlocked(p.Name, ProcessQuery.GetImagePath(p.Pid), p.Pid, self, none));
                // The estimate only sums RAM, so it skips the (costly) per-process spare checks.
                long est = SwitchPlanner.PlanGaming(all, det, snapshot, automatic: false, self, (_, _) => false).EstimatedBytes;
                // Never offer Windows, VibeShift, other users' apps, games or the launchers games depend on.
                var offers = SwitchPlanner.SuggestApps(all, det, ProcessQuery.GetImagePath, (p, path) =>
                        p.SessionId != mySession
                        || BlockList.IsBlocked(p.Name, path, p.Pid, self, none)
                        || GameRules.IsGameLauncher(p.Name)
                        || GameRules.IsCandidate(path, roots)
                        || GameRules.IsMyGame(path, snapshot.MyGames)
                        || string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new SuggestionRow(s.Path, AppCatalog.Custom(s.Path).Name, Format.Bytes(s.Bytes)))
                    .ToList();
                // How to start each running app again (for vibe apps). Background-only browsers have no window to restore.
                var seen = new Dictionary<string, ClosedApp>();
                foreach (var d in det)
                {
                    var root = SwitchPlanner.LauncherRoots(all, d).FirstOrDefault();
                    if (root is null || ProcessQuery.GetCommandLine(root.Pid)?.Contains("--no-startup-window", StringComparison.OrdinalIgnoreCase) == true) continue;
                    var exe = ProcessQuery.GetImagePath(root.Pid) is { } path ? ReopenTarget.PreferStub(path, System.IO.File.Exists) : null;
                    var target = new ClosedApp(d.Entry.Id, d.Entry.Name, exe, ProcessQuery.GetAumid(root.Pid));
                    if ((target.ExePath ?? target.Aumid) is not null) seen[d.Entry.Id] = target;
                }
                return (det, ProcessQuery.AvailableRamBytes(), est, (offers, seen));
            });
            RecordLaunchTargets(suggestions.seen);
            Apply(entries, detected, avail, estimated);
            ApplyVibe(entries, detected);
            SyncSuggestions(suggestions.offers);
        }
        catch (Exception e)
        {
            Log.Write("Refresh failed: " + e.Message);
        }
    }

    public async Task ToggleModeAsync(bool automatic)
    {
        if (IsBusy || vibeBusy || demo) return;
        IsBusy = true;
        var progress = new Progress<string>(t => BusyText = t);
        try
        {
            if (Mode == Mode.Coding)
            {
                BusyText = "Switching...";
                if (FindGames is { } find) CurrentGamePids = await Task.Run(find);
                session = await switcher.StartGamingAsync(Settings.Clone(), automatic, CurrentGamePids, progress);
                failedReopen.Clear();
                Mode = Mode.Gaming;
            }
            else
            {
                BusyText = "Switching back...";
                var closed = session ?? new SessionState();
                var result = await switcher.BackToCodingAsync(Settings.Clone(), closed, progress);
                var targets = Switcher.LaunchTargets(Settings);
                failedReopen.Clear();
                failedReopen.UnionWith(closed.Closed.Concat(targets.Values).Where(c => result.Failed.Contains(c.Name)).Select(c => c.EntryId));
                LastReturnMessage =
                    (result.Opened.Count > 0 ? "Opened " + string.Join(", ", result.Opened) + "." : "Your apps are ready.")
                    + (result.Failed.Count > 0 ? " Couldn't open " + string.Join(", ", result.Failed) + "." : "");
                session = null;
                stats = store.LoadStats();
                Mode = Mode.Coding;
            }
            Switched?.Invoke(Mode, automatic);
        }
        catch (Exception e)
        {
            Log.Write($"Switch failed: {e}");
            // The switch may have got as far as saving session.json: follow the disk, so the reopen list
            // isn't overwritten by the next switch and Back to vibe coding still works.
            session = store.LoadSession();
            Mode = session is null ? Mode.Coding : Mode.Gaming;
            Switched?.Invoke(Mode, automatic); // keep the tray in step with the mode we ended up in
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync(); // never throws
    }

    /// <summary>Saves a changed toggle and refreshes the CAN FREE estimate.</summary>
    private void OnToggle(AppRow row, bool on)
    {
        Settings.Toggles[row.Id] = on;
        if (on && Settings.GameApps.Remove(row.Id)) row.SetIsGameSilently(false); // can't close and open for gaming
        SaveSettings();
        _ = RefreshAsync();
    }

    /// <summary>"Open when gaming". An app can't be both opened and closed for gaming, so this turns Close off.</summary>
    private void OnGame(AppRow row, bool on)
    {
        Settings.GameApps.Remove(row.Id);
        if (on)
        {
            Settings.GameApps.Add(row.Id);
            Settings.Toggles[row.Id] = false;
            row.SetIsOnSilently(false);
        }
        SaveSettings();
        _ = RefreshAsync();
    }

    private void OnVibe(AppRow row, bool on)
    {
        Settings.VibeApps.Remove(row.Id);
        if (on) Settings.VibeApps.Add(row.Id);
        SaveSettings();
    }

    /// <summary>Remembers how to start each running app, so it can be opened as a vibe app later.</summary>
    private void RecordLaunchTargets(IReadOnlyDictionary<string, ClosedApp> seen)
    {
        bool changed = false;
        foreach (var (id, target) in seen)
        {
            if (Settings.LaunchTargets.TryGetValue(id, out var known) && known == target) continue;
            Settings.LaunchTargets[id] = target;
            changed = true;
        }
        if (changed && !demo) store.SaveSettings(Settings);
    }

    private void Apply(IReadOnlyList<CatalogEntry> entries, IReadOnlyList<Detected> detected, long avail, long estimated)
    {
        var running = detected.ToDictionary(d => d.Entry.Id);
        bool learned = false;
        foreach (var d in detected.Where(d => d.Entry.Kind != EntryKind.Custom && !Settings.KnownApps.Contains(d.Entry.Id)))
        {
            Settings.KnownApps.Add(d.Entry.Id);
            learned = true;
        }
        if (learned) store.SaveSettings(Settings);

        var closedIds = session?.ClosedEntryIds ?? [];
        var all = new List<AppRow>();
        foreach (var e in entries)
        {
            bool isRunning = running.TryGetValue(e.Id, out var d);
            bool listed = isRunning || e.Kind == EntryKind.Custom || Settings.KnownApps.Contains(e.Id) || closedIds.Contains(e.Id);
            if (!listed) continue;
            var row = Row(e);
            row.IsRunning = isRunning;
            row.SetIsOnSilently(Settings.IsOn(e));
            row.SetIsVibeSilently(Settings.VibeApps.Contains(e.Id));
            row.SetIsGameSilently(Settings.GameApps.Contains(e.Id));
            row.ShowToggle = IsCoding;
            row.RamText = isRunning ? Format.Bytes(d!.Bytes) : "not running";
            if (isRunning) failedReopen.Remove(e.Id);

            if (IsGaming && !isRunning && !closedIds.Contains(e.Id) && row.IsVibe)
            {
                row.IsDimmed = true;
                row.Tag = "opens after"; // a vibe app that wasn't open: it starts when you switch back
            }
            else if (IsGaming && closedIds.Contains(e.Id))
            {
                row.IsDimmed = true;
                bool wslOnly = e.Kind == EntryKind.DockerWsl && session!.Closed.All(c => c.EntryId != e.Id);
                row.Tag = e.Kind == EntryKind.Mcp ? "come back when their app restarts"
                    : wslOnly ? "starts again when needed"
                    : e.Reopen ? "reopens after" : "";
            }
            else
            {
                row.IsDimmed = false;
                row.Tag = IsGaming && isRunning ? "left running" : failedReopen.Contains(e.Id) ? "couldn't reopen" : "";
            }
            all.Add(row);
        }

        var home = all.Where(r => IsGaming
            ? r.IsRunning || closedIds.Contains(r.Id) || r.Tag == "opens after"
            : r.IsRunning || failedReopen.Contains(r.Id)).ToList();
        Sync(AllRows, all);
        Sync(HomeRows, home);

        FreeRamText = Format.Bytes(avail);
        AppsText = $"{all.Count(r => r.IsRunning)} open";
        ThirdValue = Format.Bytes(IsGaming ? session?.Freed ?? 0 : estimated);
        UpdateStats();
    }

    /// <summary>Fills the mockup's sample numbers, for --screenshot.</summary>
    public void LoadDemo(Mode demoMode)
    {
        Mode = demoMode;
        var sample = new (string Id, long Bytes, bool On)[]
        {
            ("claude-desktop", 1_181_116_006, true), ("chatgpt", 671_088_640, true), ("cursor", 943_718_400, true),
            ("mcp", 576_716_800, true), ("docker-wsl", 2_040_109_466, false),
        };
        var list = new List<AppRow>();
        foreach (var (id, bytes, on) in sample)
        {
            var row = Row(AppCatalog.BuiltIn.Single(e => e.Id == id));
            row.SetIsOnSilently(on);
            row.SetIsVibeSilently(id is "claude-desktop" or "chatgpt");
            row.IsRunning = IsCoding || !on;
            row.ShowToggle = IsCoding;
            row.RamText = Format.Bytes(bytes);
            row.IsDimmed = IsGaming && on;
            row.Tag = !IsGaming ? "" : !on ? "left running" : id == "mcp" ? "come back when their app restarts" : "reopens after";
            list.Add(row);
        }
        Sync(HomeRows, list);
        Sync(AllRows, list);
        SyncSuggestions([new(@"C:\Spotify\Spotify.exe", "Spotify", "412 MB"), new(@"C:\OBS\obs64.exe", "OBS Studio", "386 MB")]);
        var vibeSample = new (string Id, string Status)[]
        {
            ("claude-desktop", "running \u00b7 1.1 GB"), ("chatgpt", "running \u00b7 640 MB"), ("cursor", "closed"), ("ollama", "closed"), ("lmstudio", "closed"),
        };
        var vibeList = new List<VibeAppRow>();
        foreach (var (id, status) in vibeSample)
        {
            var row = new VibeAppRow(AppCatalog.BuiltIn.Single(e => e.Id == id), (_, _) => { });
            row.SetIsOpenSilently(status != "closed");
            row.Status = status;
            vibeList.Add(row);
        }
        Sync(VibeRows, vibeList);
        Raise(nameof(HasVibeRows));
        Games =
        [
            new("steam:1422450", "Deadlock", "steam://rungameid/1422450", DateTime.UtcNow),
            new("steam:730", "Counter-Strike 2", "steam://rungameid/730", DateTime.UtcNow.AddDays(-7)),
            new("steam:221100", "DayZ", "steam://rungameid/221100", DateTime.UtcNow.AddDays(-9)),
        ];
        Raise(nameof(Games));
        RaisePlay();
        StatsText = IsGaming ? "In gaming mode for 1 h 12 m" : "Last 7 days: 4 sessions · 18.2 GB freed · 9 h 12 m in gaming mode";
        PowerPlanOptions = [new("", "Don't change"), new("1", "Balanced"), new("2", "High performance"), new("3", "Power saver")];
        FreeRamText = IsGaming ? "11.4 GB" : "8.2 GB";
        AppsText = IsGaming ? "0 open" : "4 open";
        ThirdValue = "3.2 GB";
    }

    private void SyncSuggestions(IReadOnlyList<SuggestionRow> wanted)
    {
        if (Suggestions.Select(s => (s.Path, s.RamText)).SequenceEqual(wanted.Select(s => (s.Path, s.RamText)))) return;
        Suggestions.Clear();
        foreach (var s in wanted) Suggestions.Add(s);
        Raise(nameof(HasSuggestions));
    }

    private AppRow Row(CatalogEntry e)
    {
        if (!rows.TryGetValue(e.Id, out var row)) rows[e.Id] = row = new AppRow(e, demo ? null : OnToggle, demo ? null : OnVibe, demo ? null : OnGame);
        return row;
    }

    private static void Sync<T>(ObservableCollection<T> target, List<T> wanted)
    {
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var r in wanted) target.Add(r);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed record ThemeOption(ThemeChoice Value, string Label);

    public sealed class RelayCommand(Func<Task> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter)
        {
            try
            {
                await run();
            }
            catch (Exception e)
            {
                Log.Write("Command failed: " + e); // async void: an escaped exception would take the app down
            }
        }
    }
}
