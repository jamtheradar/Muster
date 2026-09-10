using Muster.Core.Presence;

namespace Muster.Graph;

/// <summary>
/// Hands out Graph access tokens for one presence account. See SPEC section 8.3.
/// </summary>
/// <remarks>
/// Split from the strategy so the strategy can be tested against a stubbed
/// <c>HttpMessageHandler</c> without MSAL, and so the settings screen can drive sign-in and
/// sign-out without going anywhere near presence.
/// </remarks>
public interface IGraphTokenProvider
{
    /// <summary>
    /// A token from the cache, refreshing it if need be. Null when the account has never signed
    /// in, when consent was withdrawn, or when the refresh token has expired — all of which mean
    /// the same thing to a caller that must not pop a window: presence cannot be written yet.
    /// </summary>
    Task<string?> GetTokenSilentAsync(PresenceAccount account, CancellationToken ct = default);

    /// <summary>
    /// Signs the account in, showing the tenant's own sign-in and consent pages. Only ever called
    /// from the settings screen: a consent dialog appearing because a call started would be
    /// indistinguishable from a phishing prompt.
    /// </summary>
    Task<GraphSignInResult> SignInAsync(PresenceAccount account, CancellationToken ct = default);

    /// <summary>Whether there is a cached account at all, for the settings screen's status column.</summary>
    Task<bool> IsSignedInAsync(PresenceAccount account, CancellationToken ct = default);

    /// <summary>Forgets the cached account and its refresh token.</summary>
    Task SignOutAsync(PresenceAccount account, CancellationToken ct = default);
}
