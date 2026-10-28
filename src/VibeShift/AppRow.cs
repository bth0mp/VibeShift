using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
    private readonly Action<AppRow, bool>? onVibe;
    private readonly Action<AppRow, bool>? onGame;
    private bool isOn, isVibe, isGame, isRunning, isDimmed, showToggle;
    private string ramText = "", tag = "";

    public AppRow(CatalogEntry entry, Action<AppRow, bool>? onToggle, Action<AppRow, bool>? onVibe = null, Action<AppRow, bool>? onGame = null)
    {
        Entry = entry;
        this.onToggle = onToggle;
        this.onVibe = onVibe;
        this.onGame = onGame;
        BadgeBrush = (Brush)new BrushConverter().ConvertFromString(entry.Color)!;
        BadgeBrush.Freeze();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CatalogEntry Entry { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string BadgeLetter => Initial(Entry.Name);

    /// <summary>First letter or digit, so a product named ".NET" gets "N", not ".".</summary>
    public static string Initial(string name) =>
        name.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpperInvariant(c).ToString() : "?";
    public string BadgeColor => Entry.Color;
    public Brush BadgeBrush { get; }
    public bool IsCustom => Entry.Kind == EntryKind.Custom;
    public const string BrowsersTooltip =
        "Closes your browser and restores its tabs when you switch back. To keep a music tab playing instead, leave this off " +
        "and let the browser sleep idle tabs: Firefox does it on its own, Chrome has Memory Saver, Edge has Sleeping tabs.";

    public string? Tooltip => Entry.Kind == EntryKind.Mcp ? McpTooltip : Entry.Id == "browsers" ? BrowsersTooltip : null;

    /// <summary>Set by the user; saves through the callback.</summary>
    public bool IsOn
    {
        get => isOn;
        set
        {
            if (Set(ref isOn, value)) onToggle?.Invoke(this, value);
        }
    }

    /// <summary>"Open when vibing": started every time you switch back. Saves through the callback.</summary>
    public bool IsVibe
    {
        get => isVibe;
        set
        {
            if (Set(ref isVibe, value)) onVibe?.Invoke(this, value);
        }
    }

    /// <summary>"Open when gaming" (Discord, OBS...): started when you start gaming, never closed for it.</summary>
    public bool IsGame
    {
        get => isGame;
        set
        {
            if (Set(ref isGame, value)) onGame?.Invoke(this, value);
        }
    }

    /// <summary>Only apps VibeShift can start again can be vibe or game apps (not MCP servers, CLI agents or Widgets).</summary>
    public bool CanVibe => Entry.Reopen;

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

    public void SetIsVibeSilently(bool value) => Set(ref isVibe, value, nameof(IsVibe));

    public void SetIsGameSilently(bool value) => Set(ref isGame, value, nameof(IsGame));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
