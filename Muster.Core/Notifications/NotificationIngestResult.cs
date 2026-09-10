namespace Muster.Core.Notifications;

/// <summary>
/// The outcome of offering a notification to the hub.
/// </summary>
/// <param name="Record">
/// The canonical record. For a duplicate this is the one already in history, not the rejected
/// candidate, so callers can still attach state to the notification the panel is showing.
/// </param>
/// <param name="IsNew">False when this duplicated a recent notification.</param>
public readonly record struct NotificationIngestResult(NotificationRecord Record, bool IsNew);
