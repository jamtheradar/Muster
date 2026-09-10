using System.Text.Json.Serialization;

namespace Muster.Core.Config;

/// <summary>
/// A pinned, persistent entry inside a workspace. Services are config, never code: there is no
/// plugin or recipe system, and there never will be. See SPEC section 2.
/// </summary>
public sealed record ServiceConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public ServiceKind Kind { get; init; } = ServiceKind.Generic;

    public required Uri Url { get; init; }

    /// <summary>Overrides the workspace profile, for two identities in one tenant.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Never suspended, and loaded at startup rather than on first visit.</summary>
    public bool KeepAlive { get; init; }

    public bool NotificationsEnabled { get; init; } = true;

    public bool Muted { get; init; }

    /// <summary>Only meaningful when <see cref="Kind"/> is <see cref="ServiceKind.Teams"/>.</summary>
    public ServicePresenceConfig? Presence { get; init; }

    /// <summary>
    /// Whether notifications from this service enter the pipeline at all. Filtering happens at
    /// ingestion, not display, so a muted service never pollutes the notification history.
    /// </summary>
    [JsonIgnore]
    public bool AcceptsNotifications => NotificationsEnabled && !Muted;

    /// <summary>
    /// Whether this service contributes to unread badges. Muting silences the interruption but
    /// keeps the count visible; turning notifications off means you do not care about it at all.
    /// </summary>
    [JsonIgnore]
    public bool ContributesUnread => NotificationsEnabled;

    /// <summary>The profile this service runs in, given its owning workspace.</summary>
    public string ResolveProfileName(WorkspaceConfig workspace) => string.IsNullOrWhiteSpace(ProfileName)
        ? workspace.ResolvedProfileName
        : ProfileName;
}
