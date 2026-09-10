namespace Muster.Core.Presence;

/// <summary>The presence state machine's states. See SPEC section 8.2.</summary>
public enum CallState
{
    /// <summary>No session is holding a microphone.</summary>
    Idle,

    /// <summary>At least one session has held a microphone long enough to count as a call.</summary>
    InCall,

    /// <summary>
    /// Everything has gone quiet, but not yet for long enough to be sure. Audio coming back
    /// during this window returns to <see cref="InCall"/> rather than starting a new call, which
    /// is what stops a device switch mid-call from flapping presence.
    /// </summary>
    Clearing,
}
