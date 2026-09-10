namespace Muster.Core.Presence;

/// <summary>
/// Supplies the live details needed to write presence for one session. Implemented by the host,
/// which is the only thing that can see a session's traffic.
/// </summary>
/// <remarks>
/// This is what keeps <see cref="PagePresenceStrategy"/> in <c>Muster.Core</c> and testable: the
/// strategy knows the presence API's shape, the host knows how to obtain the way in, and neither
/// needs the other's type. A session that has not yet made a presence call of its own simply has
/// nothing here, which is a normal state for the first few seconds after a tab comes up.
/// </remarks>
public interface ITeamsPresenceSessionSource
{
    /// <summary>What has been observed for a session, or null if it has not been seen yet.</summary>
    TeamsPresenceSession? For(string sessionId);
}
