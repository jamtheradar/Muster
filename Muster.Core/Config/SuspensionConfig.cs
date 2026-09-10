using System.Text.Json.Serialization;

namespace Muster.Core.Config;

/// <summary>Background session suspension. See SPEC section 6.1.</summary>
/// <remarks>
/// The off switch is deliberate. Suspension pauses timers and script in a page that is still
/// expected to be watching for messages, and the failure mode — a background service quietly
/// stopping — is one the user notices long after the fact and cannot easily attribute. Turning it
/// off has to be possible without a rebuild.
/// </remarks>
public sealed record SuspensionConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long a workspace has to have been off screen before its sessions are suspended.
    /// </summary>
    public int IdleMinutes { get; init; } = DefaultIdleMinutes;

    public const int DefaultIdleMinutes = 5;

    /// <summary>Longest idle window worth offering: beyond a day nothing would ever suspend.</summary>
    public const int MaxIdleMinutes = 1440;

    [JsonIgnore]
    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleMinutes);
}
