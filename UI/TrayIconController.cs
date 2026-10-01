using System.Drawing;
using System.IO;
using DevOverlay.Platform.Windows;
using Forms = System.Windows.Forms;

namespace DevOverlay.UI;

/// <summary>Owns the notification-area icon and delegates all application decisions to the host.</summary>
public sealed class TrayIconController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayIconController(Action openSettings, Action toggleOverlay, Action exitApplication)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => openSettings());
        menu.Items.Add("Show/Hide Overlay", null, (_, _) => toggleOverlay());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "DevOverlay",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => openSettings();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/DevOverlay;component/assets/DevOverlay.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UriFormatException)
        {
            RuntimeDiagnostics.Write($"[Tray] Icon resource unavailable: {exception.Message}");
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
