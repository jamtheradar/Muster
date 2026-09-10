namespace Muster.Core.Notifications;

/// <summary>One notification, as kept in history and shown in the panel.</summary>
/// <param name="Id">Unique per record. The toast activation argument carries this back.</param>
/// <param name="SessionId">The session it came from; also the tab to activate on click.</param>
/// <param name="WorkspaceId">Used to group the panel and to switch workspace on click.</param>
/// <param name="ServiceName">Display name of the originating service.</param>
public sealed record NotificationRecord(
    string Id,
    string SessionId,
    string WorkspaceId,
    string ServiceName,
    string Title,
    string Body,
    DateTimeOffset ReceivedAt,
    NotificationSource Source)
{
    /// <summary>
    /// The key duplicates are matched on. The same message can arrive on both ingestion paths,
    /// and the service worker copy carries no identity the WebView2 event shares.
    /// </summary>
    public (string, string, string) DedupeKey => (SessionId, Title, Body);
}
