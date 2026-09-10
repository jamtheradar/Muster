namespace Muster.Core.Presence;

/// <summary>
/// One write attempt against one account. Raised so the host can log it: Muster.Core takes no
/// logging dependency, and presence is the one subsystem where a silent failure is expensive.
/// </summary>
public sealed class PresenceActionEventArgs(
    PresenceAccount account,
    PresenceAction action,
    bool succeeded) : EventArgs
{
    public PresenceAccount Account { get; } = account;

    public PresenceAction Action { get; } = action;

    public bool Succeeded { get; } = succeeded;
}
