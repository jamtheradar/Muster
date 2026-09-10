using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Muster.Core.Hosting;

namespace Muster.App.Views;

/// <summary>
/// A top-level window hosting one floating session: a popped-out Teams meeting or chat, or a
/// sign-in prompt. See SPEC section 6.2.
/// </summary>
/// <remarks>
/// <para>
/// This is still Muster's own WebView2 on the workspace's own profile. Nothing here reaches the
/// system browser, which would be the wrong identity — it is the shell's window boundary that is
/// being escaped, not its session isolation.
/// </para>
/// <para>
/// The window is unowned: it sits in the z-order on its own terms, can go behind the shell, and
/// carries its own taskbar button. That is the point of popping a meeting out. It also means
/// nothing closes it automatically, so the shell closes its popups explicitly on the way out.
/// </para>
/// <para>
/// The window is shown before its session is created, and deliberately so: WebView2 needs a
/// realised window handle before <c>EnsureCoreWebView2Async</c>, and a control offered as
/// <c>e.NewWindow</c> must not have navigated yet. Showing an empty window for the moment that
/// takes is the cost of keeping <c>window.opener</c> alive.
/// </para>
/// </remarks>
public partial class PopupWindow : Window
{
    private bool _closedByPage;

    public PopupWindow()
    {
        InitializeComponent();
    }

    /// <summary>The session this window was opened for, once it exists.</summary>
    public SessionDescriptor? Descriptor { get; private set; }

    /// <summary>
    /// True when this window is a second view of a tab that is still running, which is what
    /// popping out a pinned service produces. The tab is still delivering that service's
    /// notifications, so this window must not deliver them a second time.
    /// </summary>
    public bool IsDuplicateView { get; set; }

    /// <summary>
    /// Raised when the window is going away, whoever closed it. Not <c>Closed</c>: that name
    /// belongs to <see cref="Window"/>, and shadowing it would hide the base event.
    /// </summary>
    public event EventHandler<SessionDescriptor>? SessionClosed;

    /// <summary>The panel the session's control is added to.</summary>
    public Panel Host => SessionHost;

    /// <summary>
    /// Applies the geometry the page asked for. Honoured rather than imposed: a page that says it
    /// wants 400x600 usually means it, and a meeting popped out to a second monitor is asking for
    /// a position for a reason.
    /// </summary>
    public void ApplyRequestedBounds(double? left, double? top, double? width, double? height)
    {
        if (width is > 0 && height is > 0)
        {
            Width = Math.Max(MinWidth, width.Value);
            Height = Math.Max(MinHeight, height.Value);
        }

        if (left is not null && top is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left.Value;
            Top = top.Value;
        }
        else
        {
            // CenterScreen, not CenterOwner: this window has no owner, and CenterOwner without one
            // drops it in the top-left corner.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>Colours the accent strip to match the owning workspace.</summary>
    public void ApplyAccent(Brush? accent)
    {
        if (accent is not null)
        {
            AccentStrip.Background = accent;
        }
    }

    /// <summary>Binds the window to the session now running inside it.</summary>
    public void Adopt(SessionDescriptor descriptor)
    {
        Descriptor = descriptor;
        Title = $"{descriptor.Name} — Muster";
    }

    /// <summary>Follows the page's own title, the way a browser window does.</summary>
    public void SetTitle(string? title)
        => Title = string.IsNullOrWhiteSpace(title) ? "Muster" : $"{title} — Muster";

    /// <summary>
    /// Closes in response to the page's own <c>window.close()</c>, rather than the user. Teams
    /// ends a popped-out meeting this way, so it has to actually shut the window.
    /// </summary>
    public void CloseFromPage()
    {
        _closedByPage = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Either way the session has to be torn down: a meeting window the user closes should end
        // the same way one the page closes does, and leaving the control parented to a dead window
        // would keep a renderer alive with nothing to draw into.
        if (Descriptor is { } descriptor)
        {
            SessionClosed?.Invoke(this, descriptor);
        }

        base.OnClosed(e);
    }

    /// <summary>Whether this window was closed by the page rather than by the user.</summary>
    public bool WasClosedByPage => _closedByPage;
}
