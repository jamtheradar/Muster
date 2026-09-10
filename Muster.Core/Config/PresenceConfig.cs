namespace Muster.Core.Config;

/// <summary>Global presence coordination settings. See SPEC section 8.</summary>
public sealed record PresenceConfig
{
    /// <summary>
    /// Defaults to false. Presence writes change status visible to real people in real tenants,
    /// so it is opt-in, and it should be run with the on-screen indicator only until call
    /// detection has been observed to be accurate.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Hold an audio track this long before declaring a call. Kills device-test blips.</summary>
    public int CallDetectDelaySeconds { get; init; } = 3;

    /// <summary>Stay quiet this long before clearing. Stops flapping on device switches.</summary>
    public int CallClearDelaySeconds { get; init; } = 10;

    /// <summary>
    /// ISO 8601 duration written with every preferred-presence set. Never remove it: preferred
    /// presence does not auto-revert, so a crash mid-call would leave Busy stuck everywhere.
    /// </summary>
    public string ExpirationDuration { get; init; } = "PT2H";

    /// <summary>
    /// Application (client) id of the multi-tenant app registration presence signs in with.
    /// </summary>
    /// <remarks>
    /// A public-client id is not a secret — it appears in every sign-in URL — so unlike a token
    /// it belongs in the config rather than the MSAL cache. It lives here rather than in the build
    /// because the registration is the deployer's, not this repo's, and because a tenant that
    /// refuses consent may end up pointed at a different one.
    /// </remarks>
    public string? ClientId { get; init; }
}
