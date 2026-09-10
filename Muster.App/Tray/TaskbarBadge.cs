using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Muster.Core.Notifications;

namespace Muster.App.Tray;

/// <summary>
/// The unread badge drawn over the window's taskbar button, via <see cref="TaskbarItemInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// The same count the tray icon carries, in the other place Windows lets an app put one. They
/// answer different questions: the tray badge is what you see when the window is hidden, and this
/// is what you see when it is open but behind something else.
/// </para>
/// <para>
/// Drawn with WPF rather than GDI, unlike <see cref="TrayIcon"/>, which has to hand Windows an
/// <c>HICON</c> and destroy the previous one by hand. <see cref="TaskbarItemInfo.Overlay"/> takes
/// an ordinary frozen <see cref="ImageSource"/>, so there is no unmanaged handle to leak.
/// </para>
/// <para>
/// Windows composites this over the app icon itself, so only the badge is drawn here — the tray
/// version has to paint the app icon underneath because it replaces the whole icon.
/// </para>
/// </remarks>
public sealed class TaskbarBadge
{
    /// <summary>Rendered at 32px so the badge stays crisp where Windows scales it up.</summary>
    private const int Size = 32;

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(229, 72, 77)));
    private static readonly Pen Border = FrozenPen(Color.FromRgb(20, 20, 22), 2d);
    private static readonly Brush Foreground = Frozen(new SolidColorBrush(Colors.White));

    private static readonly Typeface Face =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private readonly TaskbarItemInfo _info = new();
    private int _shown = -1;

    public TaskbarBadge(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.TaskbarItemInfo = _info;
    }

    /// <summary>
    /// Redraws the overlay for a new total. Cheap to call on every count change; a repeat of the
    /// count already showing does nothing.
    /// </summary>
    public void Update(int unread)
    {
        if (unread == _shown)
        {
            return;
        }

        _shown = unread;

        if (unread <= 0)
        {
            _info.Overlay = null;
            _info.Description = string.Empty;
            return;
        }

        _info.Overlay = Draw(TitleUnreadParser.Format(unread));

        // Read out by screen readers and shown as the taskbar button's tooltip, so the count is
        // not something you can only get at by looking closely at 16 pixels.
        _info.Description = $"{unread} unread";
    }

    /// <summary>
    /// The badge, drawn to fill the icon. Windows composites this over the app icon in a small
    /// square, so it stays square and the type is fitted to it — widening the badge instead would
    /// simply be clipped, which is what "99+" did before it was measured.
    /// </summary>
    private static ImageSource Draw(string text)
    {
        // Inset by half the pen, or the stroke is clipped by the edge of the bitmap.
        var pill = new Rect(1d, 1d, Size - 2d, Size - 2d);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(Background, Border, pill, pill.Width / 2d, pill.Height / 2d);

            var label = Fit(text, pill.Width - 7d);
            context.DrawText(label, new Point(
                pill.X + ((pill.Width - label.Width) / 2d),
                pill.Y + ((pill.Height - label.Height) / 2d)));
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        // Frozen: it crosses to the taskbar and is never touched again.
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// The largest type that still fits across <paramref name="available"/>. Measured rather than
    /// guessed from the character count, because the cap text is "99+" in every locale but the
    /// count below it is not.
    /// </summary>
    private static FormattedText Fit(string text, double available)
    {
        var label = Label(text, 19d);

        // Advance widths scale linearly with em size, so one correction lands it.
        return label.Width <= available ? label : Label(text, 19d * available / label.Width);
    }

    private static FormattedText Label(string text, double size) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        Face,
        size,
        Foreground,
        // Rendering into a 96dpi bitmap, so the text is laid out at the same scale.
        pixelsPerDip: 1d);

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color colour, double thickness)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(colour)), thickness);
        pen.Freeze();
        return pen;
    }
}
