using Forms = System.Windows.Forms;

namespace LyricsStatus;

public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _share;

    public event Action? Open;
    public event Action? ToggleShare;
    public event Action? Quit;

    public TrayIcon()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico")).Stream;
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var open = new Forms.ToolStripMenuItem("Open Lyricat") { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        open.Click += (_, _) => Open?.Invoke();
        _share = new Forms.ToolStripMenuItem("Start sharing");
        _share.Click += (_, _) => ToggleShare?.Invoke();
        var quit = new Forms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Quit?.Invoke();
        menu.Items.AddRange([open, _share, new Forms.ToolStripSeparator(), quit]);

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Lyricat",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Open?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => Open?.Invoke();
    }

    public void SetSharing(string state, bool on, bool busy)
    {
        _icon.Text = $"Lyricat · {state}";
        _share.Text = on ? "Stop sharing" : "Start sharing";
        _share.Enabled = !busy;
    }

    public void Tell(string title, string text) => _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
