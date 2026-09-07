using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SemanticStart.App;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayIconService(OverlayWindow overlay, Action showSettings, Func<Task> rebuildIndex, Action exit, AppSettings settings)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => overlay.Dispatcher.BeginInvoke(() => overlay.ShowOverlay()));
        menu.Items.Add("Rebuild index", null, async (_, _) => await rebuildIndex());
        menu.Items.Add("Settings", null, (_, _) => overlay.Dispatcher.BeginInvoke(showSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => overlay.Dispatcher.BeginInvoke(exit));

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "SemanticStart",
            Icon = AppIcon.LoadSmall(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => overlay.Dispatcher.BeginInvoke(() => overlay.ShowOverlay());
    }

    /// <summary>Surfaces a notification through the tray icon.</summary>
    public void ShowMessage(string title, string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(10000);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Showing a tray notification failed");
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
