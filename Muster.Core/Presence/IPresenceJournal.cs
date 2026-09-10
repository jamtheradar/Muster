namespace Muster.Core.Presence;

/// <summary>
/// Remembers, across process lifetimes, which accounts this app currently has showing Busy.
/// </summary>
/// <remarks>
/// <para>
/// Preferred presence is sticky and outlives the app that set it. Without a record on disk,
/// recovering from a crash mid-call would mean either leaving people Busy until the expiry ran
/// out, or clearing every configured account at startup — which would silently undo a Busy the
/// user had set for themselves in Teams. The journal is what makes the recovery precise.
/// </para>
/// <para>
/// It holds whole accounts rather than service ids so recovery does not depend on the config
/// still describing them. An account edited or deleted while the app was dead is still one this
/// app left showing Busy, and still has to be cleared.
/// </para>
/// </remarks>
public interface IPresenceJournal
{
    /// <summary>Accounts believed to be showing Busy because of this app. Never throws.</summary>
    IReadOnlyList<PresenceAccount> Read();

    /// <summary>Records the current set. Never throws; a journal that cannot be written is ignored.</summary>
    void Write(IEnumerable<PresenceAccount> accounts);
}
