using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Muster.Core.Presence;

namespace Muster.Graph;

/// <summary>
/// MSAL public-client sign-in, one application per tenant, sharing one DPAPI-protected token
/// cache. See SPEC section 8.3.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="IPublicClientApplication"/> per tenant rather than one on <c>organizations</c>:
/// the authority then names the tenant the config asked for, so a token can never come back for
/// the wrong one. That matters more here than usual, because the whole point of the app is that
/// the same person holds several accounts at once and they must not be confused.
/// </para>
/// <para>
/// Nothing in this class ever logs a token, an authorization header or a cache path's contents.
/// The cache itself is <see cref="MsalCacheHelper"/>'s, which on Windows is a DPAPI-protected
/// file under the current user.
/// </para>
/// </remarks>
public sealed class GraphTokenProvider(
    GraphPresenceOptions options,
    ILogger<GraphTokenProvider> log) : IGraphTokenProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IPublicClientApplication> _apps = new(StringComparer.OrdinalIgnoreCase);
    private MsalCacheHelper? _cache;
    private string? _cachedForClientId;
    private bool _disposed;

    public async Task<string?> GetTokenSilentAsync(PresenceAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var app = await GetApplicationAsync(account, ct).ConfigureAwait(false);

        if (app is null)
        {
            return null;
        }

        var known = await FindAccountAsync(app, account).ConfigureAwait(false);

        if (known is null)
        {
            await LogNoCachedAccountAsync(app, account).ConfigureAwait(false);
            return null;
        }

        try
        {
            var result = await app
                .AcquireTokenSilent(GraphPresenceOptions.Scopes, known)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);

            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            log.LogWarning(
                "Presence account {Account} needs signing in again ({Error}). Nothing was written.",
                account.Label,
                ex.ErrorCode);
            return null;
        }
        catch (MsalServiceException ex)
        {
            log.LogError(
                "Token request for {Account} failed: {Error} {Code}",
                account.Label,
                ex.ErrorCode,
                ex.StatusCode);
            return null;
        }
        catch (MsalClientException ex)
        {
            log.LogError(ex, "Token request for {Account} could not be made", account.Label);
            return null;
        }
    }

    public async Task<GraphSignInResult> SignInAsync(PresenceAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var app = await GetApplicationAsync(account, ct).ConfigureAwait(false);

        if (app is null)
        {
            return GraphSignInResult.Failed(
                "presence.clientId is not set, so there is no app registration to sign in to.");
        }

        try
        {
            var result = await app
                .AcquireTokenInteractive(GraphPresenceOptions.Scopes)
                .WithLoginHint(account.UserPrincipalName)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);

            log.LogInformation(
                "Presence account {Account} signed in to tenant {Tenant}",
                account.Label,
                account.TenantId);

            return GraphSignInResult.Ok(result.Account?.Username ?? account.UserPrincipalName);
        }
        catch (MsalServiceException ex) when (IsConsentFailure(ex))
        {
            // SPEC section 12: expected, and not something a retry fixes.
            log.LogWarning(
                "Tenant {Tenant} will not consent to the presence app registration ({Error}). " +
                "Set that account's strategy to none, or ask its admin to grant consent.",
                account.TenantId,
                ex.ErrorCode);

            return GraphSignInResult.NeedsAdminConsent(
                $"{account.Label}: this tenant requires an administrator to consent to " +
                $"Presence.ReadWrite for the app registration ({ex.ErrorCode}).");
        }
        catch (MsalException ex)
        {
            log.LogError(ex, "Interactive sign-in for {Account} failed", account.Label);
            return GraphSignInResult.Failed($"{account.Label}: sign-in failed ({ex.ErrorCode}).");
        }
    }

    public async Task<bool> IsSignedInAsync(PresenceAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var app = await GetApplicationAsync(account, ct).ConfigureAwait(false);

        return app is not null && await FindAccountAsync(app, account).ConfigureAwait(false) is not null;
    }

    public async Task SignOutAsync(PresenceAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var app = await GetApplicationAsync(account, ct).ConfigureAwait(false);

        if (app is null)
        {
            return;
        }

        if (await FindAccountAsync(app, account).ConfigureAwait(false) is { } known)
        {
            await app.RemoveAsync(known).ConfigureAwait(false);
            log.LogInformation("Presence account {Account} signed out", account.Label);
        }
    }

    /// <summary>
    /// A tenant's application, built on first use. Returns null when the config has no client id,
    /// which is the normal state until an app registration exists.
    /// </summary>
    private async Task<IPublicClientApplication?> GetApplicationAsync(PresenceAccount account, CancellationToken ct)
    {
        var clientId = options.ClientId;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(account.TenantId))
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // The client id is a config value and can change under a running app. Everything built
            // against the old one is worthless, cached accounts included.
            if (!string.Equals(_cachedForClientId, clientId, StringComparison.OrdinalIgnoreCase))
            {
                _apps.Clear();
                _cache = null;
                _cachedForClientId = clientId;
            }

            if (_apps.TryGetValue(account.TenantId, out var existing))
            {
                return existing;
            }

            var app = PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, account.TenantId)
                .WithRedirectUri(options.RedirectUri)
                .WithClientName("Muster")
                .Build();

            await AttachCacheAsync(app).ConfigureAwait(false);

            _apps[account.TenantId] = app;
            return app;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Registers the shared, DPAPI-protected cache. One file across every tenant: MSAL keys its
    /// entries by account and authority, and one file means one thing to back up and one thing to
    /// delete when signing everything out.
    /// </summary>
    private async Task AttachCacheAsync(IPublicClientApplication app)
    {
        try
        {
            if (_cache is null)
            {
                Directory.CreateDirectory(options.CacheDirectory);

                var storage = new StorageCreationPropertiesBuilder(
                    options.CacheFileName,
                    options.CacheDirectory).Build();

                _cache = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            }

            _cache.RegisterCache(app.UserTokenCache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MsalCachePersistenceException)
        {
            // Without persistence, sign-in survives only until the app closes. Worth saying out
            // loud, and much better than refusing to do presence at all.
            log.LogError(
                ex,
                "The MSAL token cache in {Directory} could not be opened, so sign-ins will not " +
                "survive a restart",
                options.CacheDirectory);
        }
    }

    /// <summary>
    /// Says why no token could be found, and names what the cache does hold.
    /// </summary>
    /// <remarks>
    /// <c>userPrincipalName</c> is the only key matching a configured account to a cached one, so a
    /// wrong one is indistinguishable from never having signed in: the settings row still reads
    /// "Not signed in" straight after a sign-in that visibly worked. Listing the cached usernames
    /// turns that into a one-line fix. It matters most for a guest account, where the address the
    /// tenant knows you by need not be the one you typed.
    /// </remarks>
    private async Task LogNoCachedAccountAsync(IPublicClientApplication app, PresenceAccount account)
    {
        var cached = (await app.GetAccountsAsync().ConfigureAwait(false))
            .Select(candidate => candidate.Username)
            .ToList();

        if (cached.Count == 0)
        {
            log.LogWarning(
                "Presence account {Account} has never signed in, so nothing can be written for it. " +
                "Sign in from Settings, Presence.",
                account.Label);
            return;
        }

        // The cache file is shared across tenants, so these are every sign-in this app holds, not
        // only this tenant's. Said plainly rather than implied, or the list reads as a contradiction.
        log.LogWarning(
            "Presence account {Account} has no cached sign-in for '{Upn}', so nothing can be " +
            "written for it. The token cache holds: {Cached}. If one of those is the same person, " +
            "correct presence.userPrincipalName to match it exactly.",
            account.Label,
            account.UserPrincipalName,
            string.Join(", ", cached));
    }

    private static async Task<IAccount?> FindAccountAsync(IPublicClientApplication app, PresenceAccount account)
    {
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);

        return accounts.FirstOrDefault(candidate =>
            string.Equals(candidate.Username, account.UserPrincipalName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Consent, as opposed to a bad password or a network fault.</summary>
    private static bool IsConsentFailure(MsalServiceException ex)
        => ex.ErrorCode is "consent_required" or "invalid_grant" or "unauthorized_client"
        || ex.Message.Contains("AADSTS65001", StringComparison.Ordinal)
        || ex.Message.Contains("AADSTS90094", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
