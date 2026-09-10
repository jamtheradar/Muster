namespace Muster.Core.Hosting;

/// <summary>
/// A live web session, as seen from the domain. This is the seam that keeps Muster.Core free of
/// WebView2: everything the presence and notification pipelines need from a hosted page arrives
/// through here as plain events, so they can be tested without launching a browser.
/// </summary>
/// <remarks>
/// Members are added as each milestone needs them. Do not speculatively widen this to mirror
/// CoreWebView2; if Core wants a browser type, the answer is a new abstraction, not a leak.
/// </remarks>
public interface IHostedSession
{
    SessionDescriptor Descriptor { get; }

    /// <summary>Raised on document title change. Milestone 4 parses this for unread counts.</summary>
    event EventHandler<string>? TitleChanged;

    /// <summary>Reloads the current page.</summary>
    void Reload();

    /// <summary>Navigates back to the session's configured home URL.</summary>
    void GoHome();
}
