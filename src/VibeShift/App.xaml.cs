using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeShift.Core.Catalog;
using VibeShift.Core.Games;
using VibeShift.Core.Processes;
using VibeShift.Core.State;
using VibeShift.Core.Switching;
using VibeShift.Core.Updates;

namespace VibeShift;

public partial class App : Application
{
    // Back 10 s after the game closes (a launcher restarting it doesn't bounce the apps); Auto mode counts down 10 s
    // before starting, so a misdetection or an unsaved file can be saved with one click.
    private readonly AutoModeTracker tracker = new(TimeSpan.FromSeconds(10), startDelay: TimeSpan.FromSeconds(10));
    private readonly IdleReminder reminder = new(TimeSpan.FromHours(2));
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private Dictionary<int, string> loggedGames = [];
    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private MainWindow? window;
    private MainViewModel? vm;
    private Tray? tray;
    private Hotkeys? hotkeys;
    private GameWatcher? watcher;
    private bool quitting, quitAsked, ticking, started;
    private bool leftoverSession; // started in gaming mode, and nothing has switched since
    private const string InstanceName = @"Local\VibeShift.Instance";
    private string instanceName = InstanceName;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated Extra boost helper: do the one service job and leave. Malformed --services args exit with
        // an error instead of falling through to a full (elevated) app start.
        if (e.Args.Length > 0 && e.Args[0] == "--services")
        {
            Shutdown(Services.RunHelper(e.Args));
            return;
        }
        if (Arg(e.Args, "--screenshot") is { } png)
        {
            Screenshot(png, Arg(e.Args, "--theme") ?? "dark", Arg(e.Args, "--demo") ?? "coding", Arg(e.Args, "--page") ?? "home",
                e.Args.Contains("--bottom"));
            return;
        }

        string? command = e.Args.FirstOrDefault(a => Commands.Contains(a));

        // Started from the terminal of an app VibeShift closes (Claude Code, an AI app's shell)? Closing that app for
        // gaming would end its job and VibeShift with it, mid-switch, and the app's file virtualization would hide the
        // user's real settings. Hand over to a copy Explorer starts outside it.
        if (HandedOverToExplorer(command))
        {
            Shutdown();
            return;
        }

        // One instance: a second launch just brings the first window forward.
        // A copy with its own data folder (VIBESHIFT_DATA) is its own instance, so test runs never stop the real one.
        string scope = Environment.GetEnvironmentVariable(JsonStore.DataDirVariable) is { Length: > 0 } d
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..12]
            : "";
        instanceName = InstanceName + scope;
        instance = new Mutex(true, instanceName, out bool first);
        // A second launch signals the first and leaves: plain, it shows the window; with --gaming, --coding or --toggle
        // it switches modes, so a Stream Deck button or a script can drive VibeShift.
        var signals = new[] { "Show" }.Concat(Commands.Select(c => "Cmd" + c)).Select(name =>
            new EventWaitHandle(false, EventResetMode.AutoReset, SignalName(name, scope))).ToArray();
        showSignal = signals[0];
        if (!first)
        {
            signals[command is null ? 0 : 1 + Array.IndexOf(Commands, command)].Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (true)
            {
                int i = WaitHandle.WaitAny(signals);
                Dispatcher.BeginInvoke(() =>
                {
                    if (i == 0) ShowWindow();
                    else _ = RunCommandAsync(Commands[i - 1]);
                });
            }
        }) { IsBackground = true, Name = "signals" }.Start();

        var store = JsonStore.Default;
        Log.Init(store.Dir);
        if (Environment.ProcessPath is { } exe) Updater.CleanUp(exe); // the exe an update replaced
        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Write("Unhandled: " + ev.Exception);
            ev.Handled = true; // a tray app that vanishes mid-game is worse than one that logged an error
            // ...but if startup itself failed, there's no window or tray: exit rather than linger invisibly,
            // holding the single-instance lock so every relaunch does nothing.
            if (!started) Shutdown(1);
        };
        var settings = store.LoadSettings();
        ThemeManager.Apply(settings.Theme);
        if (settings.StartWithWindows) StartupRegistration.Set(true); // refresh the path if the exe moved

        // A leftover session.json means we crashed or rebooted mid-game: the view model opens in Gaming mode.
        vm = new MainViewModel(store, settings);
        // Mid-game crash, update or reboot: keep switching back by itself once the game is gone.
        if (vm.HasSession)
        {
            // Shut down or signed out in gaming mode: the game is gone whatever it was, so this counts as it closing.
            // Not with Extra boost's services paused: restarting them takes an admin prompt, which shouldn't greet a
            // sign-in nobody asked it at.
            using var me = Process.GetCurrentProcess();
            bool signedOut = !vm.SessionPausedServices
                             && vm.SessionStartedUtc < ProcessQuery.SignInTimeUtc(ProcessSnapshot.Take(), me.SessionId);
            tracker.Resume(vm.SessionIsAutomatic, settings.ReturnWhenGameCloses, DateTime.UtcNow, signedOut);
            leftoverSession = true;
            Log.Write(signedOut ? "Started in gaming mode, from before the last sign-out" : "Started in gaming mode");
        }
        window = new MainWindow(vm);
        window.Closing += (_, ev) =>
        {
            if (quitting) return;
            ev.Cancel = true;
            window.Hide();
            Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
        };
        tray = new Tray(ShowWindow, () => vm.ToggleModeAsync(automatic: false), QuitFromTray,
            vm.OpenAllAsync, vm.CloseAllAsync, vm.VibeAppsNow, vm.SetVibeAppAsync,
            () => vm.CanPlay ? "Play " + vm.PlayTarget!.Name : null, vm.PlayAsync,
            () => vm.GameStartChoice, choice => vm.GameStartChoice = choice);
        vm.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName is nameof(MainViewModel.PlayingText)) tray?.Update(vm.Mode, vm.TrayDetail);
        };
        hotkeys = new Hotkeys(window);
        ApplyHotkeys();
        vm.SettingsChanged += ApplyHotkeys;
        vm.SettingsChanged += () => _ = vm.LoadGamesAsync();
        _ = vm.LoadGamesAsync();
        _ = vm.LoadPowerPlansAsync();
        vm.RestartRequested += RestartInto;
        tray.Update(vm.Mode, vm.TrayDetail);
        var shownMode = vm.Mode;
        vm.Switched += (mode, automatic) =>
        {
            // Raised after a failed switch too (to keep the tray in step), with the mode unchanged.
            bool changed = mode != shownMode;
            shownMode = mode;
            leftoverSession = false;
            tray.Update(mode, vm.TrayDetail);
            reminder.Reset();
            loggedGames = [];
            vm.SetPlaying([]);
            if (!automatic) tracker.ManualSwitch(mode, vm.CurrentGamePids, vm.Settings.ReturnWhenGameCloses);
            // Auto mode's start failed: leave that game alone, or the countdown comes round again every few seconds.
            else if (!changed && mode == Mode.Coding) tracker.CancelStart(vm.CurrentGamePids);
            // Back from gaming with the window out of sight: say what opened. Nothing ever pops up while you play.
            if (changed && mode == Mode.Coding && !window.IsVisible) tray.Notify("Back to vibe coding", vm.LastReturnMessage);
        };
        // "Switch back when my game closes" flipped in the middle of a session started by hand applies now, not at
        // the next switch. (A session Auto mode started switches back whatever the setting says.)
        vm.SettingsChanged += () =>
        {
            if (vm.Mode == Mode.Gaming && !vm.SessionIsAutomatic) tracker.SetReturnWhenGameCloses(vm.Settings.ReturnWhenGameCloses);
        };
        // "Not a game" mid-session: one started by hand waits for a real game again, so the app just ruled out doesn't
        // count as "the game closed" (you may be playing something VibeShift can't see). One Auto mode started because
        // of it switches back once it's gone.
        vm.NotAGameMarked += () =>
        {
            if (!vm.SessionIsAutomatic) tracker.ManualSwitch(Mode.Gaming, [], vm.Settings.ReturnWhenGameCloses);
        };
        vm.SettingsChanged += RebuildWatcher;
        vm.FindGames = () => watcher is { } w ? w.Scan(ProcessSnapshot.Take()) : [];
        RebuildWatcher();

        if (!e.Args.Contains("--tray") && command is null) window.Show();
        else Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
        _ = vm.RefreshAsync();
        if (command is not null) _ = RunCommandAsync(command);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();
        // Update check, only if the user turned it on: shortly after start, then once a day.
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(24);
            _ = vm.CheckUpdatesAsync(quiet: true);
        };
        updateTimer.Start();
        started = true;
    }

    /// <summary>
    /// Restarts VibeShift through Explorer when it shares a job with an app it may close. True once the new copy (or
    /// one already running) holds the single-instance lock; false to carry on here.
    /// </summary>
    private static bool HandedOverToExplorer(string? command)
    {
        // ponytail: Explorer passes on neither VIBESHIFT_DATA nor arguments, so test copies and --tray starts stay put,
        // and a command (--gaming, an AI agent's likeliest way to start VibeShift) is signalled to the new copy once it
        // runs, which shows its window like any plain start; a temporary .lnk could carry --tray if that ever matters
        if (Environment.GetEnvironmentVariable(JsonStore.DataDirVariable) is { Length: > 0 }
            || Environment.GetCommandLineArgs().Length > (command is null ? 1 : 2) || Environment.ProcessPath is not { } exe) return false;
        if (Mutex.TryOpenExisting(InstanceName, out var running))
        {
            running.Dispose();
            return false; // a copy is already running: this one just shows it, as usual
        }
        try
        {
            var job = ProcessQuery.MyJobPids();
            if (job.Count == 0) return false;
            var inJob = ProcessSnapshot.Take().Where(p => job.Contains(p.Pid)).ToList();
            var apps = SwitchPlanner.Detect(inJob, AppCatalog.BuiltIn, ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine, _ => false);
            if (!SwitchPlanner.SharesJobWithApp(job, Environment.ProcessId, apps)) return false;
            // Held during the hand-over, so the copy Explorer starts never hands over in turn.
            using var handover = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\VibeShift.Handover", out bool mine);
            if (!mine) return false;
            Process.Start(new ProcessStartInfo(ReopenTarget.Explorer, $"\"{exe}\"") { UseShellExecute = false })?.Dispose();
            // Explorer always "succeeds": leave only once the new copy has taken the lock.
            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(200);
                if (Mutex.TryOpenExisting(InstanceName, out var started))
                {
                    started.Dispose();
                    if (command is not null) SignalWhenReady("Cmd" + command);
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // a failed check must never stop VibeShift starting: carry on here, as before
        }
        return false;
    }

    private static string SignalName(string name, string scope) => $@"Local\VibeShift.{name.Replace("--", ".")}{scope}";

    /// <summary>
    /// Sets a signal of a copy that has just taken the lock. It creates its signals right after the lock, so wait for
    /// them to exist: one set before then would vanish with this copy.
    /// </summary>
    private static void SignalWhenReady(string name)
    {
        for (int i = 0; i < 25; i++)
        {
            if (EventWaitHandle.TryOpenExisting(SignalName(name, ""), out var signal))
            {
                using (signal) signal.Set();
                return;
            }
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// An update was swapped in: hand the single-instance lock to the new exe and leave. False if the new exe
    /// wouldn't start (antivirus, SmartScreen): this copy then takes the lock back and stays, rather than leave no
    /// VibeShift running and, in gaming mode, the closed apps with nothing to bring them back.
    /// </summary>
    private bool RestartInto(string exe)
    {
        try
        {
            instance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not held by this thread; disposing still frees it once we exit
        }
        instance?.Dispose();
        instance = null;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (window?.IsVisible != true) start.ArgumentList.Add("--tray");
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write("Restart after update failed: " + e.Message); // the next launch runs the new version
            instance = new Mutex(true, instanceName, out _);
            return false;
        }
        Quit();
        return true;
    }

    /// <summary>Ctrl+Alt+G switch modes, Ctrl+Alt+V open all AI apps, Ctrl+Alt+P play; off when the setting is.</summary>
    private void ApplyHotkeys()
    {
        if (hotkeys is null || vm is null) return;
        if (!vm.Settings.Hotkeys)
        {
            hotkeys.Unregister();
            vm.HotkeyStatus = "";
            return;
        }
        var taken = hotkeys.Register(
        [
            ('G', "Ctrl+Alt+G", () => _ = vm.ToggleModeAsync(automatic: false)),
            ('V', "Ctrl+Alt+V", () => _ = vm.OpenAllAsync()),
            ('P', "Ctrl+Alt+P", () => _ = vm.PlayAsync()),
        ]);
        vm.HotkeyStatus = taken.Count == 0 ? "" : $"{string.Join(" and ", taken)} {(taken.Count == 1 ? "is" : "are")} already used by another app.";
        if (taken.Count > 0) Log.Write("Hotkeys: " + vm.HotkeyStatus);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        hotkeys?.Dispose();
        tray?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }

    private static readonly string[] Commands = ["--gaming", "--coding", "--toggle"];

    /// <summary>
    /// --gaming / --coding / --toggle, from this copy's own command line or signalled by a second launch. Waits for the
    /// game watcher first (a fresh start hasn't found the game libraries yet), so the switch still protects a running game.
    /// </summary>
    private async Task RunCommandAsync(string command)
    {
        if (vm is null) return;
        try
        {
            for (int i = 0; i < 50 && watcher is null; i++) await Task.Delay(200);
            Log.Write("Command line: " + command);
            await vm.SwitchToAsync(command switch { "--gaming" => Mode.Gaming, "--coding" => Mode.Coding, _ => null });
            // Gaming mode left over from before this start (a shutdown, a crash) may be set to switch back within
            // seconds: after a sign-out it counts as the game closed. --gaming then did nothing, and the apps came back
            // ~10 s later (2026-11-23 review). The command says a game is about to start: wait for it, as after a click.
            // ponytail: the old session is kept as it is, so apps started since sign-in stay open; carrying its reopen
            // list into a fresh Start gaming would close them too
            if (leftoverSession && vm.Mode == Mode.Gaming)
            {
                tracker.ManualSwitch(Mode.Gaming, vm.CurrentGamePids, vm.Settings.ReturnWhenGameCloses);
                Log.Write("Gaming mode from before this start: now waiting for a game, as asked");
            }
            leftoverSession = false;
        }
        catch (Exception e)
        {
            Log.Write($"Command line {command} failed: {e.Message}");
        }
    }

    /// <summary>"Ask me": a game started. One click on the notification switches to gaming mode; ignoring it closes nothing.</summary>
    private void AskToStart(string? gamePath)
    {
        string name = gamePath is null ? "A game" : GameRules.DisplayName(gamePath, vm!.GameRoots);
        Log.Write($"Ask me: {name} started");
        tray?.Notify($"{name} started", "Click here to switch to gaming mode and close your apps.", () =>
        {
            // Only while a game still runs: the notification can be clicked from the notification centre long after
            // the game closed, and closing every app then would be a surprise. (Any game, not just this one: a
            // launcher may have handed over to the real exe since.)
            if (vm!.Mode == Mode.Coding && vm.FindGames?.Invoke().Count > 0) _ = vm.SwitchToAsync(Mode.Gaming);
            else ShowWindow();
        });
    }

    /// <summary>Refreshes the visible window and runs Auto mode and Ask me. Does nothing while hidden with both off.</summary>
    private async Task TickAsync()
    {
        if (ticking || vm is null || window is null || vm.IsBusy) return;
        ticking = true;
        try
        {
            if (window.IsVisible) await vm.RefreshAsync();
            // Watch for games starting (Auto mode) or, all through gaming mode, for the game closing (Auto mode or
            // "Switch back when my game closes") and for gaming mode outliving it.
            var s = vm.Settings;
            bool gaming = vm.Mode == Mode.Gaming;
            bool ask = s.AskOnGameStart && !s.AutoMode;
            if ((gaming || s.AutoMode || ask) && watcher is { } w)
            {
                var (found, alive) = await Task.Run(() =>
                {
                    var snapshot = ProcessSnapshot.Take();
                    var pids = w.Scan(snapshot);
                    return (snapshot.Where(p => pids.Contains(p.Pid)).Select(p => (p.Pid, p.Name, Path: ProcessQuery.GetImagePath(p.Pid))).ToList(),
                        snapshot.Select(p => p.Pid).ToHashSet());
                });
                var games = found.ToDictionary(g => g.Pid, g => g.Name);
                vm.CurrentGamePids = games.Keys.ToList();
                LogGames(games);
                vm.SetPlaying(gaming ? found.Where(g => g.Path is not null).Select(g => (GameRules.DisplayName(g.Path!, vm.GameRoots), g.Path!)).ToList() : []);
                // Automatic switches never show or activate the window; only the tray tooltip changes.
                var action = tracker.Update(vm.CurrentGamePids, vm.Mode, DateTime.UtcNow, autoStart: s.AutoMode || ask, canSwitch: vm.CanVibeAct,
                    alive: alive, askOnly: ask);
                if (action == AutoAction.StartSoon)
                    AnnounceAutoStart(found.FirstOrDefault(g => g.Path is not null).Path, vm.CurrentGamePids);
                else if (action == AutoAction.Ask)
                    AskToStart(found.FirstOrDefault(g => g.Path is not null).Path);
                else if (action != AutoAction.None)
                    await vm.ToggleModeAsync(automatic: true);
                // Counted from this start at the earliest: games before it went unseen. A session left from yesterday
                // said "no game for 2 hours" 3 s after starting, even right after --gaming (2026-11-23 live check).
                else if (gaming && vm.SessionStartedUtc is { } since
                         && reminder.Due(games.Count > 0, AcceptsNotifications(), since > startedUtc ? since : startedUtc, DateTime.UtcNow))
                {
                    Log.Write("Reminded: still in gaming mode, no game for 2 hours");
                    tray?.Notify("Still in gaming mode", "No game has run for 2 hours. Click here, then Back to vibe coding to reopen your apps.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Tick failed: " + ex.Message);
        }
        finally
        {
            ticking = false;
        }
    }

    private int watcherBuild;

    private async void RebuildWatcher()
    {
        // Every settings save lands here, and discovery takes a moment: only the latest one may set the watcher, or
        // a slow first discovery overwrites a newer one (and with it a game just added, or a "Not a game").
        int build = ++watcherBuild;
        try
        {
            // Straight away with the folders already known, so My games and "Not a game" count from the next poll
            // rather than once discovery finishes.
            if (watcher is not null) watcher = NewWatcher(vm!.GameRoots);
            var roots = await Task.Run(GameLibraries.Discover);
            if (build != watcherBuild) return;
            vm!.GameRoots = roots;
            watcher = NewWatcher(roots);
        }
        catch (Exception ex)
        {
            Log.Write("Game watcher rebuild failed: " + ex.Message);
        }
    }

    private GameWatcher NewWatcher(IReadOnlyList<string> roots)
    {
        using var me = Process.GetCurrentProcess();
        return new GameWatcher(roots, vm!.Settings.MyGames.ToList(), ProcessQuery.GetImagePath, ProcessQuery.HasVisibleWindow, me.SessionId,
            vm.Settings.NotGames.ToList());
    }

    /// <summary>Sitting in the tray (e.g. while gaming), hand unused pages back to Windows.</summary>
    private static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    /// <summary>Auto mode's countdown: "Gaming mode in 10 seconds", and a click keeps the apps open.</summary>
    private void AnnounceAutoStart(string? gamePath, IReadOnlyCollection<int> gamePids)
    {
        string name = gamePath is null ? "A game" : GameRules.DisplayName(gamePath, vm!.GameRoots);
        Log.Write($"Auto mode: {name} started, gaming mode in 10 seconds");
        tray?.Notify("Gaming mode in 10 seconds", $"{name} started. Click here to keep your apps open.", () =>
        {
            // Too late (clicked from the notification centre, or as the switch began): show Back to vibe coding.
            if (vm!.Mode == Mode.Gaming || vm.IsBusy)
            {
                ShowWindow();
                return;
            }
            // The games running now as well as those at the countdown's start: a launcher or splash exe in the game's
            // folder may have handed over to the real game since.
            tracker.CancelStart(gamePids.Concat(vm.CurrentGamePids).ToList());
            Log.Write($"Auto mode: kept apps open for {name}");
            tray?.Notify("Keeping your apps open", $"Auto mode leaves {name} alone until another game starts.");
        });
    }

    /// <summary>Games coming and going, so "why didn't it switch back?" has an answer in the log.</summary>
    private void LogGames(Dictionary<int, string> games)
    {
        foreach (var g in games.Where(g => !loggedGames.ContainsKey(g.Key))) Log.Write("Game running: " + g.Value);
        foreach (var g in loggedGames.Where(g => !games.ContainsKey(g.Key))) Log.Write("Game closed: " + g.Value);
        loggedGames = games;
    }

    /// <summary>No full-screen app, presentation or quiet hours right now (QUNS_ACCEPTS_NOTIFICATIONS).</summary>
    private static bool AcceptsNotifications() => SHQueryUserNotificationState(out int state) == 0 && state == 5;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private void ShowWindow()
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        _ = vm?.RefreshAsync();
    }

    /// <summary>
    /// The tray's Quit. In gaming mode it asks first: quitting there left the apps closed with nothing to say so
    /// (2026-11-02), until VibeShift was started again and switched back.
    /// </summary>
    private async void QuitFromTray()
    {
        if (vm is null || quitAsked) return;
        quitAsked = true;
        try
        {
            // Out of the menu's click handler first. A modal loop inside it got the menu's Enter (keyboard, or UI Automation's
            // Invoke): the dialog's default button fired unseen and reopened the apps (2026-11-05 live test).
            await Dispatcher.Yield(DispatcherPriority.Background);
            while (!vm.CanVibeAct) await Task.Delay(200); // never quit mid-switch: half the apps closed, no session yet
            if (vm.Mode == Mode.Gaming)
            {
                await vm.RefreshAsync(); // so the dialog names what is closed right now, not what was closed at the start
                var dialog = new QuitDialog(vm.ReopenNames);
                dialog.ShowDialog();
                if (dialog.Choice == QuitChoice.Cancel) return;
                // The dialog doesn't stop the app. While it was open the game may have closed and the automatic switch
                // back run (or be running), or a hotkey switched: toggling blindly would then START gaming mode and
                // close every app just before quitting. So wait, and only switch back if still in gaming mode.
                while (!vm.CanVibeAct) await Task.Delay(200);
                if (dialog.Choice == QuitChoice.ReopenAndQuit && vm.Mode == Mode.Gaming)
                {
                    await vm.ToggleModeAsync(automatic: false);
                    if (vm.Mode == Mode.Gaming)
                    {
                        Log.Write("Quit cancelled: the switch back didn't finish");
                        ShowWindow();
                        return;
                    }
                }
                if (vm.Mode == Mode.Gaming) Log.Write("Quit in gaming mode: apps left closed");
            }
            Log.Write("Quit");
            Quit();
        }
        catch (Exception e)
        {
            Log.Write("Quit failed: " + e.Message);
        }
        finally
        {
            quitAsked = false;
        }
    }

    private void Quit()
    {
        quitting = true;
        window?.Close();
        Shutdown();
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>--screenshot: renders the window with the mockup's sample data to a 2x PNG and exits. Touches no processes.</summary>
    private void Screenshot(string png, string theme, string demoMode, string page, bool bottom)
    {
        ThemeManager.Apply(theme == "light" ? ThemeChoice.Light : ThemeChoice.Dark, highContrast: theme == "contrast");
        var demoVm = new MainViewModel(new JsonStore(Path.GetTempPath()), new Settings(), demo: true);
        demoVm.LoadDemo(demoMode == "gaming" ? Mode.Gaming : Mode.Coding);
        // --page quit: the dialog Quit shows in gaming mode
        Window w = page == "quit" ? new QuitDialog(["Claude", "Wallpaper Engine", "PowerToys"]) : new MainWindow(demoVm);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -10000;
        w.Top = -10000;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.Topmost = false;
        (w as MainWindow)?.ShowPage(page);
        w.ContentRendered += (_, _) =>
        {
            if (bottom && w is MainWindow main) // --bottom: the end of the Settings page, which is taller than the window
            {
                main.SettingsScroll.ScrollToEnd();
                w.UpdateLayout();
            }
            var root = (FrameworkElement)w.Content;
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * 2), (int)(root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bmp.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var file = File.Create(png)) encoder.Save(file);
            Shutdown();
        };
        w.Show();
    }
}
