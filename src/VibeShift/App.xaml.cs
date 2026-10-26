using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeShift.Core.Games;
using VibeShift.Core.State;

namespace VibeShift;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Arg(e.Args, "--screenshot") is { } png)
        {
            Screenshot(png, Arg(e.Args, "--theme") ?? "dark", Arg(e.Args, "--demo") ?? "coding");
            return;
        }

        var store = JsonStore.Default;
        Log.Init(store.Dir);
        var settings = store.LoadSettings();
        ThemeManager.Apply(settings.Theme);
        var vm = new MainViewModel(store, settings);
        var window = new MainWindow(vm);
        window.Closed += (_, _) => Shutdown();
        window.Show();
        _ = vm.RefreshAsync();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) =>
        {
            if (window.IsVisible) await vm.RefreshAsync();
        };
        timer.Start();
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>--screenshot: renders the window with the mockup's sample data to a 2x PNG and exits. Touches no processes.</summary>
    private void Screenshot(string png, string theme, string demoMode)
    {
        ThemeManager.Apply(theme == "light" ? ThemeChoice.Light : ThemeChoice.Dark);
        var vm = new MainViewModel(new JsonStore(Path.GetTempPath()), new Settings(), demo: true);
        vm.LoadDemo(demoMode == "gaming" ? Mode.Gaming : Mode.Coding);
        var w = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
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
