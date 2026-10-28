using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using VibeShift.Core.Games;

namespace VibeShift;

/// <summary>
/// The icon by the clock (or under the ^ arrow). A small dot shows the mode at a glance: green for vibe coding,
/// purple for gaming. Left-click opens the window; right-click switches or quits.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem toggleItem;
    private readonly Icon codingIcon, gamingIcon;

    /// <param name="aiApps">The Vibe page's apps and whether each is running, checked when the menu opens.</param>
    /// <param name="setApp">Opens (true) or closes (false) one app by id.</param>
    public Tray(
        Action open,
        Func<Task> toggle,
        Action quit,
        Func<Task> openAll,
        Func<Task> closeAll,
        Func<IReadOnlyList<(string Id, string Name, bool Running)>> aiApps,
        Func<string, bool, Task> setApp)
    {
        var menu = new ContextMenuStrip();
        toggleItem = new ToolStripMenuItem("Start gaming", null, async (_, _) => await toggle());
        menu.Items.Add(toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open all AI apps", null, async (_, _) => await openAll());
        menu.Items.Add("Close all AI apps", null, async (_, _) => await closeAll());
        var appsItem = new ToolStripMenuItem("AI apps");
        appsItem.DropDownItems.Add(new ToolStripMenuItem("(checking...)") { Enabled = false });
        appsItem.DropDownOpening += (_, _) =>
        {
            // ✓ = running; click to open or close it, without opening the window.
            appsItem.DropDownItems.Clear();
            var apps = aiApps();
            foreach (var (id, name, running) in apps)
                appsItem.DropDownItems.Add(new ToolStripMenuItem(name, null, async (_, _) => await setApp(id, !running)) { Checked = running });
            if (apps.Count == 0) appsItem.DropDownItems.Add(new ToolStripMenuItem("No AI apps yet") { Enabled = false });
        };
        menu.Items.Add(appsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open VibeShift", null, (_, _) => open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/VibeShift.ico")).Stream)
        using (var app = new Icon(stream, SystemInformation.SmallIconSize))
        {
            codingIcon = WithDot(app, Color.FromArgb(0x34, 0xd3, 0x99));
            gamingIcon = WithDot(app, Color.FromArgb(0xa8, 0x55, 0xf7));
        }
        icon = new NotifyIcon
        {
            Icon = codingIcon,
            ContextMenuStrip = menu,
            Text = "VibeShift: Vibe coding mode",
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) open();
        };
        icon.BalloonTipClicked += (_, _) => open();
    }

    public void Update(Mode mode)
    {
        bool gaming = mode == Mode.Gaming;
        icon.Icon = gaming ? gamingIcon : codingIcon;
        icon.Text = gaming ? "VibeShift: Gaming mode" : "VibeShift: Vibe coding mode";
        toggleItem.Text = gaming ? "Back to vibe coding" : "Start gaming";
    }

    /// <summary>A Windows notification, e.g. "Back to vibe coding: opened Claude, ChatGPT". Only used once you've stopped playing.</summary>
    public void Notify(string title, string text) => icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        codingIcon.Dispose();
        gamingIcon.Dispose();
    }

    /// <summary>The app icon with a mode-coloured dot (white-ringed so it shows on any taskbar) in the corner.</summary>
    private static Icon WithDot(Icon app, Color dot)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(app, new Rectangle(Point.Empty, size));
            int d = Math.Max(6, size.Width / 2);
            var ring = new Rectangle(size.Width - d, size.Height - d, d - 1, d - 1);
            using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, ring);
            ring.Inflate(-Math.Max(1, d / 6), -Math.Max(1, d / 6));
            using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, ring);
        }
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // the clone owns its own handle
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
