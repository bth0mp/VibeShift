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
    private readonly AutoModeTracker tracker = new(TimeSpan.FromSeconds(10));
    private readonly IdleReminder reminder = new(TimeSpan.FromHours(2));
    private Dictionary<int, string> loggedGames = [];
    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private MainWindow? window;
    private MainViewModel? vm;
    private Tray? tray;
    private Hotkeys? hotkeys;
    private GameWatcher? watcher;
    private bool quitting, ticking, started;
    private const string InstanceName = @"Local\VibeShift.Instance";

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

        // Started from the terminal of an app VibeShift closes (Claude Code, an AI app's shell)? Closing that app for
        // gaming would end its job and VibeShift with it, mid-switch, and the app's file virtualization would hide the
        // user's real settings. Hand over to a copy Explorer starts outside it.
        if (HandedOverToExplorer())
        {
            Shutdown();
            return;
        }

        // One instance: a second launch just brings the first window forward.
        // A copy with its own data folder (VIBESHIFT_DATA) is its own instance, so test runs never stop the real one.
        string scope = Environment.GetEnvironmentVariable(JsonStore.DataDirVariable) is { Length: > 0 } d
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..12]
            : "";
        instance = new Mutex(true, InstanceName + scope, out bool first);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VibeShift.Show" + scope);
        if (!first)
        {
            showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true, Name = "show-signal" }.Start();

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
        // Mid-game crash or reboot: keep switching back by itself once the game is gone.
        if (vm.HasSession) tracker.Resume(vm.SessionIsAutomatic, settings.ReturnWhenGameCloses, DateTime.UtcNow);
        window = new MainWindow(vm);
        window.Closing += (_, ev) =>
        {
            if (quitting) return;
            ev.Cancel = true;
            window.Hide();
            Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
        };
        tray = new Tray(ShowWindow, () => vm.ToggleModeAsync(automatic: false), Quit,
            vm.OpenAllAsync, vm.CloseAllAsync, vm.VibeAppsNow, vm.SetVibeAppAsync,
            () => vm.CanPlay ? "Play " + vm.PlayTarget!.Name : null, vm.PlayAsync);
        hotkeys = new Hotkeys(window);
        ApplyHotkeys();
        vm.SettingsChanged += ApplyHotkeys;
        vm.SettingsChanged += () => _ = vm.LoadGamesAsync();
        _ = vm.LoadGamesAsync();
        _ = vm.LoadPowerPlansAsync();
        vm.RestartRequested += RestartInto;
        tray.Update(vm.Mode);
        vm.Switched += (mode, automatic) =>
        {
            tray.Update(mode);
            reminder.Reset();
            loggedGames = [];
            if (!automatic) tracker.ManualSwitch(mode, vm.CurrentGamePids, vm.Settings.ReturnWhenGameCloses);
            // Back from gaming with the window out of sight: say what opened. Nothing ever pops up while you play.
            if (mode == Mode.Coding && !window.IsVisible) tray.Notify("Back to vibe coding", vm.LastReturnMessage);
        };
        vm.SettingsChanged += RebuildWatcher;
        vm.FindGames = () => watcher is { } w ? w.Scan(ProcessSnapshot.Take()) : [];
        RebuildWatcher();

        if (!e.Args.Contains("--tray")) window.Show();
        else Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
        _ = vm.RefreshAsync();

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
    private static bool HandedOverToExplorer()
    {
        // ponytail: Explorer passes on neither VIBESHIFT_DATA nor arguments, so test copies and --tray starts stay put;
        // a temporary .lnk could carry the arguments if tray starts from a terminal ever matter
        if (Environment.GetEnvironmentVariable(JsonStore.DataDirVariable) is { Length: > 0 }
            || Environment.GetCommandLineArgs().Length > 1 || Environment.ProcessPath is not { } exe) return false;
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

    /// <summary>An update was swapped in: hand the single-instance lock to the new exe and leave.</summary>
    private void RestartInto(string exe)
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
        }
        Quit();
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

    /// <summary>Refreshes the visible window and runs Auto mode. Does nothing while hidden with Auto mode off.</summary>
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
            if ((gaming || s.AutoMode) && watcher is { } w)
            {
                var games = await Task.Run(() =>
                {
                    var snapshot = ProcessSnapshot.Take();
                    var found = w.Scan(snapshot);
                    return snapshot.Where(p => found.Contains(p.Pid)).ToDictionary(p => p.Pid, p => p.Name);
                });
                vm.CurrentGamePids = games.Keys.ToList();
                LogGames(games);
                // Automatic switches never show or activate the window; only the tray tooltip changes.
                if (tracker.Update(vm.CurrentGamePids, vm.Mode, DateTime.UtcNow, autoStart: s.AutoMode, canSwitch: vm.CanVibeAct) != AutoAction.None)
                    await vm.ToggleModeAsync(automatic: true);
                else if (gaming && vm.SessionStartedUtc is { } since
                         && reminder.Due(games.Count > 0, AcceptsNotifications(), since, DateTime.UtcNow))
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

    private async void RebuildWatcher()
    {
        try
        {
            var games = vm!.Settings.MyGames.ToList();
            var roots = await Task.Run(GameLibraries.Discover);
            vm.GameRoots = roots;
            using var me = Process.GetCurrentProcess();
            watcher = new GameWatcher(roots, games, ProcessQuery.GetImagePath, ProcessQuery.HasVisibleWindow, me.SessionId);
        }
        catch (Exception ex)
        {
            Log.Write("Game watcher rebuild failed: " + ex.Message);
        }
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
        ThemeManager.Apply(theme == "light" ? ThemeChoice.Light : ThemeChoice.Dark);
        var demoVm = new MainViewModel(new JsonStore(Path.GetTempPath()), new Settings(), demo: true);
        demoVm.LoadDemo(demoMode == "gaming" ? Mode.Gaming : Mode.Coding);
        var w = new MainWindow(demoVm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        w.ShowPage(page);
        w.ContentRendered += (_, _) =>
        {
            if (bottom) // --bottom: the end of the Settings page, which is taller than the window
            {
                w.SettingsScroll.ScrollToEnd();
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
