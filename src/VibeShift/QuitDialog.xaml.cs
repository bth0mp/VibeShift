using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace VibeShift;

public enum QuitChoice { Cancel, Quit, ReopenAndQuit }

/// <summary>Quit while in gaming mode: the closed apps would stay closed, so offer to reopen them first.</summary>
public partial class QuitDialog : Window
{
    public QuitChoice Choice { get; private set; } = QuitChoice.Cancel;

    /// <param name="closed">What Back to vibe coding would reopen, e.g. Claude, PowerToys.</param>
    public QuitDialog(IReadOnlyList<string> closed)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyChrome(this);
        Body.Text = closed.Count == 0
            ? "You're in gaming mode. Quit now and what VibeShift changed for gaming stays that way until you start VibeShift again and switch back."
            : $"You're in gaming mode, so {Join(closed)} {(closed.Count == 1 ? "is" : "are")} still closed. Quit now and "
              + $"{(closed.Count == 1 ? "it stays" : "they stay")} closed until you start VibeShift again and switch back.";
        Loaded += (_, _) => ReopenButton.Focus();
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private void Reopen_Click(object sender, RoutedEventArgs e) => Close(QuitChoice.ReopenAndQuit);

    private void Quit_Click(object sender, RoutedEventArgs e) => Close(QuitChoice.Quit);

    private void Close(QuitChoice choice)
    {
        Choice = choice;
        Close();
    }
}
