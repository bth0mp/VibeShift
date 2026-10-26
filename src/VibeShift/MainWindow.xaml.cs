using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace VibeShift;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => ThemeManager.ApplyChrome(this);
    }

    public void ShowPage(string page) =>
        (page switch { "apps" => AppsTab, "settings" => SettingsTab, _ => HomeTab }).IsChecked = true;

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        AppsTab.IsChecked = true;
        if (PickExe("Add an app for Gaming mode to close") is { } path) vm.AddApp(path);
    }

    private void AddSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SuggestionRow row) vm.AddApp(row.Path);
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppRow row) vm.RemoveApp(row);
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        if (PickExe("Add a game for Auto mode") is { } path) vm.AddGame(path);
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is string path) vm.RemoveGame(path);
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        e.Handled = true;
    }

    private string? PickExe(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "Programs (*.exe)|*.exe" };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
