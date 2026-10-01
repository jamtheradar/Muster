using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muster.App.Configuration;
using Muster.Core.Notifications;
using Forms = System.Windows.Forms;

namespace Muster.App.Tray;

/// <summary>
/// The notification-area icon and its unread badge. Uses the in-box WinForms
/// <see cref="Forms.NotifyIcon"/>; no WinForms UI is ever shown.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private readonly ILogger<TrayIcon> _log;
    private readonly AppIcons _icons;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _forceBusy;
    private Icon _baseIcon;
    private Icon? _composed;
    private int _shownCount = -1;
    private bool _announced;
    private bool _disposed;

    public TrayIcon(AppIcons icons, ILogger<TrayIcon> log)
    {
        _log = log;
        _icons = icons;
        _baseIcon = LoadBaseIcon(icons, log);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show Muster", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());

        // SPEC section 8.4. In the tray rather than the window because the case it exists for is a
        // call in something that is not this app, and because it is also the escape hatch when
        // automatic clearing misbehaves — which is a bad moment to be hunting for a window.
        _forceBusy = new Forms.ToolStripMenuItem("Force Busy everywhere")
        {
            CheckOnClick = false,
            ToolTipText = "Show Busy in every configured tenant until turned off. Ctrl+Alt+B.",
        };
        _forceBusy.Click += (_, _) => ForceBusyRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_forceBusy);

        // Here as well as in the window, because the moment you want it is mid-call with Teams
        // full screen over the top of everything.
        menu.Items.Add(
            "Check mic and speakers...",
            null,
            (_, _) => DeviceCheckRequested?.Invoke(this, EventArgs.Empty));

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));

        // Reachable even when the window is hidden, which is the state the app is most likely to
        // be in when something has gone wrong and the log is what you want.
        menu.Items.Add("Open logs folder", null, (_, _) => LogsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _baseIcon,
            Text = "Muster",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        Update(0);

        // The set is chosen in settings and hot-reloads, so the icon this was built with is only
        // the first one.
        _icons.Changed += OnIconSetChanged;
    }

    /// <summary>The user asked to bring the window back.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>The user asked to exit for real, as opposed to closing to tray.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>The user asked to open the settings screen.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>The user asked for the log folder, without going through the window.</summary>
    public event EventHandler? LogsRequested;

    /// <summary>The user asked to test their microphone and speakers.</summary>
    public event EventHandler? DeviceCheckRequested;

    /// <summary>The user asked to turn the manual presence override on or off.</summary>
    public event EventHandler? ForceBusyRequested;

    /// <summary>
    /// Reflects the override's real state, which is the applier's rather than the menu's: the
    /// toggle can fail, and a tick that says Busy when nothing was written would be worse than no
    /// tick at all.
    /// </summary>
    public void SetForceBusy(bool on)
    {
        if (_disposed)
        {
            return;
        }

        _forceBusy.Checked = on;
        _forceBusy.Text = on ? "Forcing Busy everywhere" : "Force Busy everywhere";
    }

    /// <summary>
    /// Tells the user where the app went the first time it closes to tray. Windows 11 hides new
    /// notification icons behind the chevron by default, so without this the window simply
    /// vanishes and the app looks like it has quit.
    /// </summary>
    public void AnnounceHiddenOnce()
    {
        if (_disposed || _announced)
        {
            return;
        }

        _announced = true;
        _notifyIcon.ShowBalloonTip(
            5000,
            "Muster is still running",
            "Your sessions stay signed in. Click the tray icon to bring the window back, or use Quit to exit.",
            Forms.ToolTipIcon.Info);
    }

    /// <summary>Redraws the badge for a new total. Cheap to call on every count change.</summary>
    public void Update(int unread)
    {
        if (_disposed || unread == _shownCount)
        {
            return;
        }

        _shownCount = unread;
        _notifyIcon.Text = unread > 0 ? $"Muster — {unread} unread" : "Muster";
        _log.LogDebug("Tray badge now {Text}", _notifyIcon.Text);

        var previous = _composed;
        _composed = unread > 0 ? Compose(_baseIcon, unread) : null;
        _notifyIcon.Icon = _composed ?? _baseIcon;

        Destroy(previous);
    }

    /// <summary>
    /// Swaps the artwork underneath the badge. The count is redrawn rather than kept, because the
    /// badge is composited onto the base icon and so belongs to the icon it was drawn over.
    /// </summary>
    private void OnIconSetChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var previousBase = _baseIcon;
        _baseIcon = LoadBaseIcon(_icons, _log);

        var previousBadge = _composed;
        _composed = _shownCount > 0 ? Compose(_baseIcon, _shownCount) : null;
        _notifyIcon.Icon = _composed ?? _baseIcon;

        Destroy(previousBadge);

        // Loaded from a file rather than GetHicon, so Dispose is the whole of it.
        previousBase.Dispose();
    }

    /// <summary>Draws the count over the app icon, the way a taskbar overlay badge looks.</summary>
    private static Icon Compose(Icon baseIcon, int count)
    {
        const int size = 32;
        var text = TitleUnreadParser.Format(count);

        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using (var source = new Icon(baseIcon, size, size))
            {
                graphics.DrawIcon(source, new Rectangle(0, 0, size, size));
            }

            // Wide enough for the text, anchored bottom right, clear of the mark itself.
            var width = text.Length <= 1 ? 17 : text.Length == 2 ? 21 : 25;
            const int height = 17;
            var badge = new Rectangle(size - width, size - height, width, height);

            using var background = new SolidBrush(Color.FromArgb(229, 72, 77));
            using var border = new Pen(Color.FromArgb(20, 20, 22), 2f);
            using var path = RoundedRectangle(badge, height / 2);
            graphics.FillPath(background, path);
            graphics.DrawPath(border, path);

            using var font = new Font("Segoe UI", text.Length <= 2 ? 10f : 8f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            graphics.DrawString(text, font, Brushes.White, badge, format);
        }

        // GetHicon hands back an unmanaged handle that Icon.Dispose does not free, so the caller
        // has to DestroyIcon it. Update() does that when it swaps in the next one.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon LoadBaseIcon(AppIcons icons, ILogger log)
    {
        var path = icons.CurrentPath;

        try
        {
            if (File.Exists(path))
            {
                return new Icon(path);
            }

            log.LogWarning("Tray icon {Path} is missing, falling back to the system icon", path);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not load tray icon {Path}", path);
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static void Destroy(Icon? icon)
    {
        if (icon is null)
        {
            return;
        }

        var handle = icon.Handle;
        icon.Dispose();
        DestroyIcon(handle);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icons.Changed -= OnIconSetChanged;

        // Hide before disposing, or Windows leaves a ghost icon in the tray until hovered.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Destroy(_composed);
        _baseIcon.Dispose();
        _log.LogInformation("Tray icon removed");
    }
}
