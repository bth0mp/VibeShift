using System.Windows.Media;

namespace VibeShift;

/// <summary>One entry in the Apps page's "Using the most RAM" list.</summary>
public sealed class SuggestionRow(string path, string name, string ramText)
{
    private static readonly Brush Badge = Freeze(new SolidColorBrush(Color.FromRgb(0x8b, 0x5c, 0xf6)));

    public string Path { get; } = path;
    public string Name { get; } = name;
    public string RamText { get; } = ramText;
    public string BadgeLetter => AppRow.Initial(Name);
    public Brush BadgeBrush => Badge;

    public override string ToString() => Name;

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
