using System;
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
using VibeShift.Core.Games;
using VibeShift.Core.Processes;
using VibeShift.Core.State;
using VibeShift.Core.Switching;

namespace VibeShift;

public partial class App : Application
{
    private readonly AutoModeTracker tracker = new(TimeSpan.FromSeconds(10));
    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private MainWindow? window;
    private MainViewModel? vm;
    private Tray? tray;
    private GameWatcher? watcher;
    private bool quitting, ticking, started;

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
            Screenshot(png, Arg(e.Args, "--theme") ?? "dark", Arg(e.Args, "--demo") ?? "coding", Arg(e.Args, "--page") ?? "home");
            return;
        }

        // One instance: a second launch just brings the first window forward.
        // A copy with its own data folder (VIBESHIFT_DATA) is its own instance, so test runs never stop the real one.
        string scope = Environment.GetEnvironmentVariable(JsonStore.DataDirVariable) is { Length: > 0 } d
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..12]
            : "";
        instance = new Mutex(true, @"Local\VibeShift.Instance" + scope, out bool first);
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
        if (vm.HasSession && (vm.SessionIsAutomatic || settings.ReturnWhenGameCloses)) tracker.ResumeAutoSession(DateTime.UtcNow);
        window = new MainWindow(vm);
        window.Closing += (_, ev) =>
        {
            if (quitting) return;
            ev.Cancel = true;
            window.Hide();
            Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
        };
        tray = new Tray(ShowWindow, () => vm.ToggleModeAsync(automatic: false), Quit,
            vm.OpenAllAsync, vm.CloseAllAsync, vm.VibeAppsNow, vm.SetVibeAppAsync);
        tray.Update(vm.Mode);
        vm.Switched += (mode, automatic) =>
        {
            tray.Update(mode);
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
        started = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
            // Watch for games starting (Auto mode) or, while gaming, for the game closing (Auto mode or
            // "Switch back when my game closes").
            var s = vm.Settings;
            bool watchStart = s.AutoMode && vm.Mode == Mode.Coding;
            bool watchEnd = vm.Mode == Mode.Gaming && (s.AutoMode || s.ReturnWhenGameCloses);
            if ((watchStart || watchEnd) && watcher is { } w)
            {
                var games = await Task.Run(() => w.Scan(ProcessSnapshot.Take()));
                vm.CurrentGamePids = games;
                // Automatic switches never show or activate the window; only the tray tooltip changes.
                if (tracker.Update(games, vm.Mode, DateTime.UtcNow, autoStart: s.AutoMode) != AutoAction.None)
                    await vm.ToggleModeAsync(automatic: true);
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
    private void Screenshot(string png, string theme, string demoMode, string page)
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
