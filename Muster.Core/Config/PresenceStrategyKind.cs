namespace Muster.Core.Config;

/// <summary>How presence is applied for one Teams account. See SPEC section 8.3.</summary>
public enum PresenceStrategyKind
{
    /// <summary>Presence sync disabled for this account.</summary>
    None,

    /// <summary>Microsoft Graph <c>setUserPreferredPresence</c>. The primary strategy.</summary>
    Graph,

    /// <summary>
    /// Teams' own presence service, called as the signed-in tab with that tab's own credentials.
    /// The fallback for a tenant that will not consent to an app registration — which, in this
    /// deployment, is most of them. Unsupported and undocumented, so it reads presence back after
    /// every write rather than trusting a 2xx, and unlike Graph it has no expiry to fall back on.
    /// </summary>
    Page,

    /// <summary>
    /// Drive the Teams status menu from injected script. Reserved in the schema and rejected by
    /// the validator: it is the fallback for a tenant that will not consent to the app
    /// registration, and it is not built. It would be the only DOM scraping in the app, so it is
    /// a deliberate decision to take rather than something to grow into.
    /// </summary>
    Dom,
}
