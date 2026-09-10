using Microsoft.Web.WebView2.Core;
using Muster.Core.Notifications;

namespace Muster.App.Hosting;

/// <summary>
/// A notification as it leaves a session, before deduplication. Carries the live WebView2 object
/// when there is one, which is what lets a later click be reported back to the page.
/// </summary>
public sealed class RaisedNotification(
    string sessionId,
    string title,
    string body,
    string? tag,
    NotificationSource source,
    CoreWebView2Notification? live)
{
    public string SessionId { get; } = sessionId;

    public string Title { get; } = title;

    public string Body { get; } = body;

    /// <summary>The page's own grouping key, if it supplied one. Informational for now.</summary>
    public string? Tag { get; } = tag;

    public NotificationSource Source { get; } = source;

    /// <summary>
    /// Only present for the <see cref="NotificationSource.WebView"/> path. The service worker
    /// shim sees the payload but has nothing to report back to.
    /// </summary>
    public CoreWebView2Notification? Live { get; } = live;
}
