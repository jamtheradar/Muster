namespace Muster.Core.Presence;

/// <summary>A transition of the call state machine.</summary>
public sealed class PresenceStateChangedEventArgs(
    CallState previous,
    CallState current,
    string? sourceSessionId,
    IReadOnlySet<string> callSessions) : EventArgs
{
    public CallState Previous { get; } = previous;

    public CallState Current { get; } = current;

    /// <summary>The session whose microphone started this call, if any.</summary>
    public string? SourceSessionId { get; } = sourceSessionId;

    /// <summary>
    /// Every session that has held audio during this call. These are the ones that must not be
    /// set Busy: they are already in the call.
    /// </summary>
    public IReadOnlySet<string> CallSessions { get; } = callSessions;

    public bool IsInCall => Current is CallState.InCall or CallState.Clearing;
}
