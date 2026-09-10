namespace Muster.Core.Config;

/// <summary>Notification surface settings. See SPEC section 7.</summary>
public sealed record NotificationConfig
{
    public bool ToastsEnabled { get; init; } = true;

    /// <summary>How many entries the notification panel keeps.</summary>
    public int HistoryLimit { get; init; } = DefaultHistoryLimit;

    public const int DefaultHistoryLimit = 200;
}
