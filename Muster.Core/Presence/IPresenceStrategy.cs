using Muster.Core.Config;

namespace Muster.Core.Presence;

/// <summary>
/// How presence is actually written for one account. See SPEC section 8.3.
/// </summary>
/// <remarks>
/// The interface exists so the strategy is selectable per account and can be switched off
/// entirely: a tenant that refuses consent for an unverified publisher gets
/// <see cref="PresenceStrategyKind.None"/> rather than a broken app.
/// </remarks>
public interface IPresenceStrategy
{
    /// <summary>Which <c>presenceStrategy</c> config value this implements.</summary>
    PresenceStrategyKind Kind { get; }

    /// <summary>
    /// Sets preferred presence to Busy.
    /// </summary>
    /// <param name="expirationDuration">
    /// ISO 8601, from <c>presence.expirationDuration</c>. Never optional: preferred presence does
    /// not auto-revert, so without an expiry a crash mid-call leaves Busy stuck across every
    /// tenant until someone notices.
    /// </param>
    /// <returns>False if presence could not be written. Never throws for an expected failure.</returns>
    Task<bool> SetBusyAsync(PresenceAccount account, string expirationDuration, CancellationToken ct);

    /// <summary>Clears the preferred presence this app set. The normal end of a call.</summary>
    Task<bool> ClearAsync(PresenceAccount account, CancellationToken ct);
}
