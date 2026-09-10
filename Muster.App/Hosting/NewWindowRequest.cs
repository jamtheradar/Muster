using Microsoft.Web.WebView2.Core;

namespace Muster.App.Hosting;

/// <summary>
/// A page asking for a new window, handed to the shell to route. The originating session keeps
/// ownership of the WebView2 deferral and waits on <see cref="Completion"/>, so a handler that
/// throws can never leave the page hung.
/// </summary>
public sealed class NewWindowRequest
{
    private readonly CoreWebView2NewWindowRequestedEventArgs _args;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal NewWindowRequest(HostedSession source, CoreWebView2NewWindowRequestedEventArgs args)
    {
        Source = source;
        _args = args;
        Target = Uri.TryCreate(args.Uri, UriKind.Absolute, out var parsed)
            ? parsed
            : new Uri("about:blank");
    }

    /// <summary>The session the request came from.</summary>
    public HostedSession Source { get; }

    /// <summary>The URL the page wants to open.</summary>
    public Uri Target { get; }

    /// <summary>
    /// True when the page called <c>window.open</c> with window features rather than following a
    /// <c>target=_blank</c> link. Entra sign-in arrives this way, and must get its own session so
    /// <c>window.opener</c> stays live.
    /// </summary>
    public bool IsScriptedPopup => _args.WindowFeatures.HasSize || _args.WindowFeatures.HasPosition;

    public bool IsUserInitiated => _args.IsUserInitiated;

    /// <summary>The geometry the page asked for, or null where it did not ask.</summary>
    /// <remarks>
    /// Only meaningful alongside <see cref="IsScriptedPopup"/>: the features are zero when the
    /// page did not set them, and a window sized 0x0 is not what anyone meant.
    /// </remarks>
    public double? RequestedLeft => _args.WindowFeatures.HasPosition ? _args.WindowFeatures.Left : null;

    public double? RequestedTop => _args.WindowFeatures.HasPosition ? _args.WindowFeatures.Top : null;

    public double? RequestedWidth => _args.WindowFeatures.HasSize ? _args.WindowFeatures.Width : null;

    public double? RequestedHeight => _args.WindowFeatures.HasSize ? _args.WindowFeatures.Height : null;

    internal Task Completion => _completion.Task;

    /// <summary>
    /// Hands a freshly created, not-yet-navigated session to the page as the new window. This is
    /// what preserves <c>window.opener</c> and lets <c>window.close()</c> come back to us.
    /// </summary>
    public void Adopt(CoreWebView2 core) => _args.NewWindow = core;

    /// <summary>Signals that routing is finished, successfully or not.</summary>
    public void Complete() => _completion.TrySetResult();
}
