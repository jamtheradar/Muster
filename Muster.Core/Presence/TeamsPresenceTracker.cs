using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muster.Core.Presence;

/// <summary>
/// Watches each session's presence traffic and keeps what is needed to write presence as that
/// session. Implements <see cref="ITeamsPresenceSessionSource"/> for
/// <see cref="PagePresenceStrategy"/>.
/// </summary>
/// <remarks>
/// <para>
/// In <c>Muster.Core</c> rather than the host because the interesting part is judgement, not
/// plumbing: which URL shape gives the regional API base, and — the one that can go badly wrong —
/// which observed identity is actually yours.
/// </para>
/// <para>
/// Nothing here is persisted. A token lives in memory for as long as its session does and is
/// dropped with <see cref="Forget"/> when the session goes away.
/// </para>
/// </remarks>
public sealed partial class TeamsPresenceTracker : ITeamsPresenceSessionSource
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TeamsPresenceSession> _sessions = new(StringComparer.Ordinal);

    public TeamsPresenceSession? For(string sessionId)
    {
        lock (_sync)
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>Every session currently able to write presence. For diagnostics only.</summary>
    public IReadOnlyList<string> KnownSessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// Folds one observed request into what is known about its session.
    /// </summary>
    /// <returns>True if this observation taught us something new.</returns>
    public bool Observe(TeamsPresenceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (BaseOf(observation.Uri) is not { } endpoint)
        {
            return false;
        }

        // A request with no authorization tells us the endpoint and nothing else, and must not
        // overwrite a good token with nothing.
        var authorization = Blank(observation.Authorization);
        var cookie = Blank(observation.Cookie);
        var mri = OwnMriFrom(observation);

        lock (_sync)
        {
            var known = _sessions.GetValueOrDefault(observation.SessionId);

            var updated = new TeamsPresenceSession(
                Endpoint: endpoint,
                Authorization: authorization ?? known?.Authorization ?? string.Empty,
                Cookie: cookie ?? known?.Cookie ?? string.Empty,

                // First identity seen wins. Teams asks for its own presence before anyone else's,
                // and a later single-subject call could legitimately be about a colleague.
                Mri: known?.Mri ?? mri);

            if (updated == known)
            {
                return false;
            }

            _sessions[observation.SessionId] = updated;
            return true;
        }
    }

    /// <summary>Drops everything held for a session. Called when its tab goes away.</summary>
    public void Forget(string sessionId)
    {
        lock (_sync)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <summary>Drops everything. Called on the way out.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _sessions.Clear();
        }
    }

    /// <summary>
    /// The regional API base for a presence URL: <c>https://host/ups/{region}/v{n}/</c>.
    /// </summary>
    /// <remarks>
    /// Derived rather than constructed, every time. The region is the tenant's, not a constant,
    /// and a hardcoded <c>apac</c> would fail for a client homed elsewhere without saying so.
    /// </remarks>
    public static Uri? BaseOf(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return null;
        }

        var match = ApiBase().Match(uri.AbsolutePath);

        return match.Success
            ? new Uri(new Uri(uri.GetLeftPart(UriPartial.Authority)), match.Value)
            : null;
    }

    /// <summary>
    /// Reads this session's own identity out of a <c>getpresence</c> request, or null.
    /// </summary>
    /// <remarks>
    /// Single-subject calls only, deliberately. The same endpoint fetches colleagues' presence in
    /// batches, and adopting one of those as your own identity would mean reading somebody else's
    /// status to decide whether your own write worked — wrong, and confidently so.
    /// </remarks>
    public static string? OwnMriFrom(TeamsPresenceObservation observation)
    {
        if (observation.Body is not { Length: > 0 } body
            || !observation.Uri.AbsolutePath.Contains("/presence/getpresence", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 1)
            {
                return null;
            }

            var first = document.RootElement[0];

            return first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("mri", out var mri)
                && mri.ValueKind == JsonValueKind.String
                ? mri.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"^/ups/[A-Za-z0-9-]+/v\d+/", RegexOptions.IgnoreCase)]
    private static partial Regex ApiBase();
}
