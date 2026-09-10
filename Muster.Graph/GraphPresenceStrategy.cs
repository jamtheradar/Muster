using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muster.Core.Config;
using Muster.Core.Presence;

namespace Muster.Graph;

/// <summary>
/// Writes preferred presence through Microsoft Graph. The primary strategy; see SPEC section 8.3.
/// </summary>
/// <remarks>
/// <para>
/// The requests go to <c>/me/presence/...</c> rather than SPEC's <c>/users/{userId}/presence/...</c>.
/// Delegated <c>Presence.ReadWrite</c> only ever permits the signed-in user anyway, and the token
/// already names them, so <c>/me</c> removes the one way this code could write to the wrong
/// person's status. The account's UPN is still what picks the token out of the MSAL cache.
/// </para>
/// <para>
/// Failures are reported, never thrown. Presence is a convenience layered on top of a shell whose
/// job is to keep sessions alive, and a Graph outage must not be able to disturb that.
/// </para>
/// </remarks>
public sealed class GraphPresenceStrategy : IPresenceStrategy
{
    /// <summary>The Graph endpoint. Overridden in tests by setting the client's base address.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://graph.microsoft.com/v1.0/");

    private static readonly JsonSerializerOptions ResponseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IGraphTokenProvider _tokens;
    private readonly HttpClient _http;
    private readonly ILogger<GraphPresenceStrategy> _log;

    public GraphPresenceStrategy(
        IGraphTokenProvider tokens,
        HttpClient http,
        ILogger<GraphPresenceStrategy> log)
    {
        _tokens = tokens;
        _http = http;
        _log = log;

        _http.BaseAddress ??= DefaultBaseAddress;
    }

    public PresenceStrategyKind Kind => PresenceStrategyKind.Graph;

    public async Task<bool> SetBusyAsync(PresenceAccount account, string expirationDuration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrWhiteSpace(expirationDuration))
        {
            // The validator will not let this through, and it is worth refusing rather than
            // guessing: a set with no expiry is the one that leaves someone Busy indefinitely.
            _log.LogError(
                "Refusing to set Busy for {Account} without an expirationDuration",
                account.Label);
            return false;
        }

        if (await GetTokenAsync(account, ct).ConfigureAwait(false) is not { } token)
        {
            return false;
        }

        await WarnIfNoPresenceSessionAsync(account, token, ct).ConfigureAwait(false);

        // Busy/Busy rather than anything more descriptive: InACall is a reported activity, not a
        // settable preferred one, so asking for it is rejected.
        var body = new PreferredPresenceRequest("Busy", "Busy", expirationDuration);

        return await PostAsync(
            account,
            token,
            "me/presence/setUserPreferredPresence",
            body,
            $"set Busy for {expirationDuration}",
            ct).ConfigureAwait(false);
    }

    public async Task<bool> ClearAsync(PresenceAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (await GetTokenAsync(account, ct).ConfigureAwait(false) is not { } token)
        {
            return false;
        }

        return await PostAsync(
            account,
            token,
            "me/presence/clearUserPreferredPresence",
            body: null,
            "clear preferred presence",
            ct).ConfigureAwait(false);
    }

    private async Task<string?> GetTokenAsync(PresenceAccount account, CancellationToken ct)
    {
        // Silent only. An interactive prompt raised because a call started would arrive with no
        // explanation and look exactly like a phishing window; sign-in belongs to the settings
        // screen, where the user asked for it.
        var token = await _tokens.GetTokenSilentAsync(account, ct).ConfigureAwait(false);

        if (token is null)
        {
            _log.LogWarning(
                "No usable token for {Account}, so its presence was left alone",
                account.Label);
        }

        return token;
    }

    /// <summary>
    /// Preferred presence only takes effect while the user has an active presence session, which
    /// in this app means a signed-in Teams tab. SPEC section 8.3 asks for the check to be made and
    /// logged rather than assumed. It is not a reason to skip the write: the preference sticks and
    /// applies as soon as a session appears.
    /// </summary>
    private async Task WarnIfNoPresenceSessionAsync(PresenceAccount account, string token, CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Get, "me/presence", token);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "Could not read current presence for {Account} ({Status}); setting Busy anyway",
                    account.Label,
                    (int)response.StatusCode);
                return;
            }

            var current = await response.Content
                .ReadFromJsonAsync<PresenceResponse>(ResponseOptions, ct)
                .ConfigureAwait(false);

            var availability = current?.Availability ?? "unknown";

            if (availability is "Offline" or "PresenceUnknown")
            {
                _log.LogWarning(
                    "{Account} has no active presence session (availability {Availability}), so " +
                    "Busy will not show until that Teams tab is signed in",
                    account.Label,
                    availability);
            }
            else
            {
                _log.LogDebug("{Account} presence is {Availability}", account.Label, availability);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _log.LogDebug(ex, "Presence check for {Account} failed; setting Busy anyway", account.Label);
        }
    }

    private async Task<bool> PostAsync(
        PresenceAccount account,
        string token,
        string path,
        PreferredPresenceRequest? body,
        string what,
        CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Post, path, token);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _log.LogInformation("Graph: {What} for {Account}", what, account.Label);
                return true;
            }

            // The body carries Graph's own error code and message, which is what makes a consent
            // or licensing failure diagnosable. It never contains the token.
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            _log.LogError(
                "Graph refused to {What} for {Account}: {Status} {Detail}",
                what,
                account.Label,
                (int)response.StatusCode,
                Trim(detail));

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogError(ex, "Graph request to {What} for {Account} did not complete", what, account.Label);
            return false;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string Trim(string detail)
        => detail.Length <= 500 ? detail : detail[..500] + "…";

    private sealed record PreferredPresenceRequest(
        [property: JsonPropertyName("availability")] string Availability,
        [property: JsonPropertyName("activity")] string Activity,
        [property: JsonPropertyName("expirationDuration")] string ExpirationDuration);

    private sealed record PresenceResponse(string? Availability, string? Activity);
}
