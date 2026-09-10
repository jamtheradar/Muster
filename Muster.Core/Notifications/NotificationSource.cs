namespace Muster.Core.Notifications;

/// <summary>Which of the two ingestion paths a notification arrived on. See SPEC section 7.1.</summary>
public enum NotificationSource
{
    /// <summary>
    /// <c>CoreWebView2.NotificationReceived</c>. Non-persistent notifications only, and the only
    /// path that can report back to the page.
    /// </summary>
    WebView,

    /// <summary>
    /// The <c>showNotification</c> shim in bridge.js. Covers service worker notifications, which
    /// never reach the WebView2 event at all.
    /// </summary>
    ServiceWorker,
}
