namespace Muster.Core.Presence;

/// <summary>
/// The narrowest possible logging seam, so a strategy living in <c>Muster.Core</c> can say what
/// happened without the project taking a logging dependency it has managed without everywhere else.
/// </summary>
/// <remarks>
/// Presence is the one subsystem where a silent failure is expensive, which is why this exists at
/// all rather than the usual pattern of raising an event for the host to log. Implementations must
/// never be handed a secret: strategies pass account labels and status codes, never headers.
/// </remarks>
public interface IPresenceStrategyLog
{
    void Info(string message);

    void Warn(string message);

    /// <summary>Discards everything. For tests, and for a strategy constructed without a host.</summary>
    public static IPresenceStrategyLog Silent { get; } = new SilentLog();

    private sealed class SilentLog : IPresenceStrategyLog
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }
    }
}
