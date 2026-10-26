using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using VibeShift.Core.Games;

namespace VibeShift;

/// <summary>The icon by the clock. Left-click opens the window; right-click switches or quits.</summary>
public sealed class Tray : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem toggleItem;

    public Tray(Action open, Func<Task> toggle, Action quit)
    {
        var menu = new ContextMenuStrip();
        toggleItem = new ToolStripMenuItem("Start gaming", null, async (_, _) => await toggle());
        menu.Items.Add(toggleItem);
        menu.Items.Add("Open VibeShift", null, (_, _) => open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/VibeShift.ico")).Stream;
        icon = new NotifyIcon
        {
            Icon = new Icon(stream, SystemInformation.SmallIconSize),
            ContextMenuStrip = menu,
            Text = "VibeShift: Vibe coding mode",
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) open();
        };
    }

    public void Update(Mode mode)
    {
        bool gaming = mode == Mode.Gaming;
        icon.Text = gaming ? "VibeShift: Gaming mode" : "VibeShift: Vibe coding mode";
        toggleItem.Text = gaming ? "Back to vibe coding" : "Start gaming";
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
