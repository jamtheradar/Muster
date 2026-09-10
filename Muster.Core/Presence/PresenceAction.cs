namespace Muster.Core.Presence;

/// <summary>What the applier did, or tried to do, to one account.</summary>
public enum PresenceAction
{
    /// <summary>Preferred presence set to Busy.</summary>
    Set,

    /// <summary>Preferred presence cleared.</summary>
    Clear,

    /// <summary>Busy re-sent before its expiry ran out, on a call longer than the expiry window.</summary>
    Renew,
}
