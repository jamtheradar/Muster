namespace Muster.Core.Presence;

/// <summary>
/// One presence request seen leaving a session, reduced to the parts worth keeping.
/// </summary>
/// <remarks>
/// The host produces these from its own network interception; <see cref="TeamsPresenceTracker"/>
/// turns a stream of them into something writable. Splitting it this way is what lets the tracker
/// — including the rule for picking your own identity out of the traffic — be unit tested without
/// a browser anywhere near it.
/// </remarks>
/// <param name="SessionId">The session the request came from.</param>
/// <param name="Uri">Absolute request URI, from which the regional API base is derived.</param>
/// <param name="Authorization">The <c>authorization</c> header value, or null. Secret.</param>
/// <param name="Cookie">The <c>Cookie</c> header value, or null. Secret.</param>
/// <param name="Body">Request body, used only to recognise a single-subject getpresence call.</param>
public sealed record TeamsPresenceObservation(
    string SessionId,
    Uri Uri,
    string? Authorization,
    string? Cookie,
    string? Body);
