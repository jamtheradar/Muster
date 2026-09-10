using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Muster.Core.Config;

namespace Muster.Core.Presence;

/// <summary>
/// Sets presence through Teams' own presence service, as the signed-in tab, using that tab's own
/// credentials. The fallback for a tenant that will not consent to an app registration.
/// </summary>
/// <remarks>
/// <para>
/// This is unsupported and undocumented, and it will break. Microsoft owes us nothing here: the
/// route was found by watching what Teams itself does when you change your status by hand. It
/// exists because most client tenants in this deployment block third-party app consent outright,
/// which leaves the Graph strategy unusable and this the only way presence sync exists at all.
/// </para>
/// <para>
/// It is deliberately not DOM automation. Nothing here reads or clicks the Teams UI; it calls the
/// same endpoint the UI calls, which is both more stable than React markup and — the part that
/// matters — able to say whether it worked.
/// </para>
/// <para>
/// <b>There is no expiry.</b> Graph's <c>expirationDuration</c> has no equivalent here: a forced
/// availability set through this route stays until something clears it. That makes
/// <see cref="PresenceApplier"/>'s journal and clear-on-startup the only protection against a
/// crash mid-call leaving Busy set indefinitely, rather than a backup to a server-side expiry.
/// </para>
/// </remarks>
public sealed class PagePresenceStrategy(
    ITeamsPresenceSessionSource sessions,
    HttpClient http,
    IPresenceStrategyLog log,
    TimeSpan? confirmationWindow = null,
    TimeSpan? confirmationInterval = null) : IPresenceStrategy
{
    /// <summary>What Teams sends for "Busy". Not <c>InACall</c>: that is a reported activity, not a settable one.</summary>
    public const string BusyAvailability = "Busy";

    /// <summary>
    /// Every availability that counts as "the Busy we asked for took effect".
    /// </summary>
    /// <remarks>
    /// Read-back compares against this set rather than against "Busy" exactly, because what comes
    /// back is the *effective* presence, not the value we wrote. An idle machine reports
    /// <c>BusyIdle</c>, and a genuine meeting in that tenant reports <c>InAMeeting</c> — both mean
    /// the force landed, and treating either as failure would report a working write as broken.
    /// The names are Teams' own, taken from its client bundle.
    /// </remarks>
    public static readonly IReadOnlySet<string> BusyFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Busy",
        "BusyIdle",
        "DoNotDisturb",
        "InACall",
        "InAMeeting",
        "InAConferenceCall",
        "Presenting",
        "UrgentInterruptionsOnly",
    };

    /// <summary>
    /// How long to keep asking before deciding a write did not take.
    /// </summary>
    /// <remarks>
    /// The first attempt at this read it back 125ms after the write and reported a stale value as
    /// a failure. Effective presence is recomputed and pushed asynchronously — the tab hears about
    /// it over a pubsub long-poll — so an immediate read is reading the past.
    /// </remarks>
    public static readonly TimeSpan DefaultConfirmationWindow = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan DefaultConfirmationInterval = TimeSpan.FromSeconds(1);

    // Overridable so a test can assert the give-up path without actually waiting six seconds for
    // it. Nothing in the app passes these.
    private readonly TimeSpan _window = confirmationWindow ?? DefaultConfirmationWindow;
    private readonly TimeSpan _interval = confirmationInterval ?? DefaultConfirmationInterval;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PresenceStrategyKind Kind => PresenceStrategyKind.Page;

    /// <summary>
    /// Sets Busy, then reads presence back to confirm it took.
    /// </summary>
    /// <param name="expirationDuration">
    /// Ignored, and it has to be: this route has no expiry parameter. Kept in the signature
    /// because the interface is shared with the Graph strategy, where it is load-bearing.
    /// </param>
    public async Task<bool> SetBusyAsync(PresenceAccount account, string expirationDuration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (Resolve(account) is not { } session)
        {
            return false;
        }

        var written = await WriteAvailabilityAsync(
            account,
            session,
            """{"availability":"Busy"}""",
            "set Busy",
            ct).ConfigureAwait(false);

        if (!written)
        {
            return false;
        }

        // A 2xx means the request was accepted, not that your status changed. Since this whole
        // route is undocumented, accepted-but-ignored is a real possibility and the failure it
        // would produce is the worst kind: you believe you are Busy and you are not.
        return await ConfirmAsync(account, session, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the forced availability, putting presence back under Teams' own control.
    /// </summary>
    /// <remarks>
    /// The empty body is the whole trick, and it is not guessable: Teams' own "Reset status" sends
    /// a PUT to this path with no content. Sending <c>{"availability":"Available"}</c> instead
    /// would look like it worked while actually pinning you Available — so you would show
    /// available during a genuine meeting in that tenant, which is worse than never clearing.
    /// </remarks>
    public async Task<bool> ClearAsync(PresenceAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (Resolve(account) is not { } session)
        {
            return false;
        }

        // Not confirmed by reading back, unlike a set. Once released, availability returns to
        // whatever Teams computes, and that can legitimately be Busy — the user really might be
        // in a meeting in this tenant. There is nothing to assert against.
        return await WriteAvailabilityAsync(account, session, string.Empty, "release", ct).ConfigureAwait(false);
    }

    private TeamsPresenceSession? Resolve(PresenceAccount account)
    {
        var session = sessions.For(account.ServiceId);

        if (session is null || !session.IsUsable)
        {
            log.Warn(
                $"{account.Label}: no presence call has been seen from that tab yet, so there is " +
                "nothing to write with. It is signed out, still loading, or asleep.");
            return null;
        }

        return session;
    }

    private async Task<bool> WriteAvailabilityAsync(
        PresenceAccount account,
        TeamsPresenceSession session,
        string body,
        string what,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri(session.Endpoint, "me/forceavailability/"));

            Authorise(request, session);

            // An empty body still carries a content type, which is what Teams sends for a reset.
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                log.Info($"Teams presence: {what} for {account.Label}");
                return true;
            }

            log.Warn($"{account.Label}: Teams refused to {what} ({(int)response.StatusCode}).");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.Warn($"{account.Label}: the request to {what} did not complete ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>Reads presence back, for as long as it takes, and checks the write took effect.</summary>
    private async Task<bool> ConfirmAsync(
        PresenceAccount account,
        TeamsPresenceSession session,
        CancellationToken ct)
    {
        if (!session.CanVerify)
        {
            // The identity comes from watching a getpresence call, which may not have happened
            // yet. Reported rather than silently assumed good: an unverified write is exactly the
            // thing this strategy is supposed to be honest about.
            log.Warn($"{account.Label}: set Busy, but could not confirm it — this tab's identity is not known yet.");
            return true;
        }

        var deadline = DateTimeOffset.UtcNow + _window;
        string? reported = null;

        try
        {
            while (true)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(session.Endpoint, "presence/getpresence/"));

                Authorise(request, session);
                request.Content = JsonContent.Create(new[] { new PresenceQuery(session.Mri!, "ups") });

                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Says nothing about the write, so it is not held against it.
                    log.Warn($"{account.Label}: set Busy, but reading it back failed ({(int)response.StatusCode}).");
                    return true;
                }

                reported = await ReadAvailabilityAsync(response, session.Mri!, ct).ConfigureAwait(false);

                if (reported is not null && BusyFamily.Contains(reported))
                {
                    log.Info($"Teams presence: {account.Label} confirmed {reported}");
                    return true;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }

            // Accepted and ignored, and given time to prove otherwise. This is the failure the
            // whole read-back exists for, and the way we find out the route has moved.
            log.Warn(
                $"{account.Label}: Teams accepted Busy but still reports '{reported ?? "nothing"}' " +
                $"after {_window.TotalSeconds:0}s. Treating it as failed.");

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.Warn($"{account.Label}: set Busy, but reading it back failed ({ex.GetType().Name}).");
            return true;
        }
    }

    /// <summary>
    /// Picks this session's own availability out of a getpresence response.
    /// </summary>
    /// <remarks>
    /// Matched on MRI rather than taken positionally. The same endpoint serves colleagues'
    /// presence, and reading the wrong entry would produce a confident, wrong answer.
    /// </remarks>
    private static async Task<string?> ReadAvailabilityAsync(
        HttpResponseMessage response,
        string mri,
        CancellationToken ct)
    {
        var entries = await response.Content
            .ReadFromJsonAsync<List<PresenceEntry>>(ReadOptions, ct)
            .ConfigureAwait(false);

        return entries?
            .FirstOrDefault(entry => string.Equals(entry.Mri, mri, StringComparison.OrdinalIgnoreCase))?
            .Presence?.Availability;
    }

    private static void Authorise(HttpRequestMessage request, TeamsPresenceSession session)
    {
        // TryAddWithoutValidation throughout: these are verbatim header values captured from a
        // real request, and the client must not reformat or reject them.
        request.Headers.TryAddWithoutValidation("authorization", session.Authorization);

        if (!string.IsNullOrEmpty(session.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.Cookie);
        }
    }

    private sealed record PresenceQuery(
        [property: JsonPropertyName("mri")] string Mri,
        [property: JsonPropertyName("source")] string Source);

    private sealed record PresenceEntry(string? Mri, PresenceValue? Presence);

    private sealed record PresenceValue(string? Availability, string? Activity);
}
