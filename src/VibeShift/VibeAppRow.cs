using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VibeShift.Core.Catalog;

namespace VibeShift;

/// <summary>One app on the Vibe page: a live switch that opens or closes it right now.</summary>
public sealed class VibeAppRow : INotifyPropertyChanged
{
    private readonly Action<VibeAppRow, bool> onSwitch;
    private bool isOpen, isBusy;
    private string status = "";

    public VibeAppRow(CatalogEntry entry, Action<VibeAppRow, bool> onSwitch)
    {
        Entry = entry;
        this.onSwitch = onSwitch;
        BadgeBrush = (Brush)new BrushConverter().ConvertFromString(entry.Color)!;
        BadgeBrush.Freeze();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CatalogEntry Entry { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string BadgeLetter => AppRow.Initial(Entry.Name);
    public Brush BadgeBrush { get; }

    /// <summary>The live switch: flipping it opens or closes the app right now.</summary>
    public bool IsOpen
    {
        get => isOpen;
        set
        {
            if (Set(ref isOpen, value)) onSwitch(this, value);
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        set { if (Set(ref isBusy, value)) Raise(nameof(CanSwitch)); }
    }

    public bool CanSwitch => !IsBusy;

    public string Status
    {
        get => status;
        set { if (Set(ref status, value)) Raise(nameof(IsRunningStatus)); }
    }

    /// <summary>Green status text while running.</summary>
    public bool IsRunningStatus => Status.StartsWith("running", StringComparison.Ordinal);

    public void SetIsOpenSilently(bool value) => Set(ref isOpen, value, nameof(IsOpen));

    public override string ToString() => Name;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
