using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using VibeShift.Core;
using VibeShift.Core.Catalog;
using VibeShift.Core.Games;
using VibeShift.Core.Processes;
using VibeShift.Core.State;
using VibeShift.Core.Switching;

namespace VibeShift;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly JsonStore store;
    private readonly Switcher switcher;
    private readonly Dictionary<string, AppRow> rows = [];
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
        MyGames = new ObservableCollection<string>(settings.MyGames);
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
        Settings.CustomApps.RemoveAll(p => AppCatalog.Custom(p).Id == row.Id);
        Settings.Toggles.Remove(row.Id);
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
            foreach (var n in new[] { nameof(IsGaming), nameof(IsCoding), nameof(ModeText), nameof(ThirdLabel), nameof(ListTitle), nameof(PrimaryText) })
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
            if (Set(ref isBusy, value)) { Raise(nameof(IsIdle)); Raise(nameof(PrimaryText)); }
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
            var (detected, avail, estimated) = await Task.Run(() =>
            {
                var all = ProcessSnapshot.Take();
                // Same block rules as a real switch, so the numbers shown match what would happen.
                var det = SwitchPlanner.Detect(all, entries, ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
                    p => p.SessionId != mySession || BlockList.IsBlocked(p.Name, ProcessQuery.GetImagePath(p.Pid), p.Pid, self, new HashSet<int>()));
                // The estimate only sums RAM, so it skips the (costly) per-process spare checks.
                long est = SwitchPlanner.PlanGaming(all, det, snapshot, automatic: false, self, (_, _) => false).EstimatedBytes;
                return (det, ProcessQuery.AvailableRamBytes(), est);
            });
            Apply(entries, detected, avail, estimated);
        }
        catch (Exception e)
        {
            Log.Write("Refresh failed: " + e.Message);
        }
    }

    public async Task ToggleModeAsync(bool automatic)
    {
        if (IsBusy || demo) return;
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
                var failed = await switcher.BackToCodingAsync(Settings.Clone(), closed, progress);
                failedReopen.Clear();
                failedReopen.UnionWith(closed.Closed.Where(c => failed.Contains(c.Name)).Select(c => c.EntryId));
                session = null;
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
        SaveSettings();
        _ = RefreshAsync();
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
            row.ShowToggle = IsCoding;
            row.RamText = isRunning ? Format.Bytes(d!.Bytes) : "not running";
            if (isRunning) failedReopen.Remove(e.Id);

            if (IsGaming && closedIds.Contains(e.Id))
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

        var home = all.Where(r => IsGaming ? r.IsRunning || closedIds.Contains(r.Id) : r.IsRunning || failedReopen.Contains(r.Id)).ToList();
        Sync(AllRows, all);
        Sync(HomeRows, home);

        FreeRamText = Format.Bytes(avail);
        AppsText = $"{all.Count(r => r.IsRunning)} open";
        ThirdValue = Format.Bytes(IsGaming ? session?.Freed ?? 0 : estimated);
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
            row.IsRunning = IsCoding || !on;
            row.ShowToggle = IsCoding;
            row.RamText = Format.Bytes(bytes);
            row.IsDimmed = IsGaming && on;
            row.Tag = !IsGaming ? "" : !on ? "left running" : id == "mcp" ? "come back when their app restarts" : "reopens after";
            list.Add(row);
        }
        Sync(HomeRows, list);
        Sync(AllRows, list);
        FreeRamText = IsGaming ? "11.4 GB" : "8.2 GB";
        AppsText = IsGaming ? "0 open" : "4 open";
        ThirdValue = "3.2 GB";
    }

    private AppRow Row(CatalogEntry e)
    {
        if (!rows.TryGetValue(e.Id, out var row)) rows[e.Id] = row = new AppRow(e, demo ? null : OnToggle);
        return row;
    }

    private static void Sync(ObservableCollection<AppRow> target, List<AppRow> wanted)
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
