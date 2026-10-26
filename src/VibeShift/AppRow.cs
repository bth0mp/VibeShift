using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VibeShift.Core.Catalog;

namespace VibeShift;

/// <summary>One app row on the Home and Apps pages.</summary>
public sealed class AppRow : INotifyPropertyChanged
{
    public const string McpTooltip =
        "Stops every MCP server, including ones used by apps that stay open. Those apps get their tools back when they restart.";

    private readonly Action<AppRow, bool>? onToggle;
    private bool isOn, isRunning, isDimmed, showToggle;
    private string ramText = "", tag = "";

    public AppRow(CatalogEntry entry, Action<AppRow, bool>? onToggle)
    {
        Entry = entry;
        this.onToggle = onToggle;
        BadgeBrush = (Brush)new BrushConverter().ConvertFromString(entry.Color)!;
        BadgeBrush.Freeze();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CatalogEntry Entry { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string BadgeLetter => Entry.Name[..1].ToUpperInvariant();
    public string BadgeColor => Entry.Color;
    public Brush BadgeBrush { get; }
    public bool IsCustom => Entry.Kind == EntryKind.Custom;
    public string? Tooltip => Entry.Kind == EntryKind.Mcp ? McpTooltip : null;

    /// <summary>Set by the user; saves through the callback.</summary>
    public bool IsOn
    {
        get => isOn;
        set
        {
            if (Set(ref isOn, value)) onToggle?.Invoke(this, value);
        }
    }

    public bool IsRunning { get => isRunning; set => Set(ref isRunning, value); }
    public bool IsDimmed { get => isDimmed; set => Set(ref isDimmed, value); }
    public bool ShowToggle { get => showToggle; set => Set(ref showToggle, value); }

    public string RamText
    {
        get => ramText;
        set { if (Set(ref ramText, value)) Raise(nameof(ShowRam)); }
    }

    public string Tag
    {
        get => tag;
        set { if (Set(ref tag, value)) { Raise(nameof(HasTag)); Raise(nameof(ShowRam)); } }
    }

    public bool HasTag => Tag.Length > 0;
    public bool ShowRam => !HasTag && RamText.Length > 0;

    /// <summary>What screen readers announce for the list item.</summary>
    public override string ToString() => Name;

    /// <summary>Reflects saved state without calling the save callback.</summary>
    public void SetIsOnSilently(bool value) => Set(ref isOn, value, nameof(IsOn));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
