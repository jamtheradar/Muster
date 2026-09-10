namespace Muster.Graph;

/// <summary>
/// Where the Graph strategy signs in from. Mutable, and updated from the config on every reload:
/// the app registration is a config value, and the composition root is built before the config
/// has been read.
/// </summary>
public sealed class GraphPresenceOptions
{
    /// <summary>
    /// Application (client) id of the multi-tenant app registration. Null until the config has
    /// been loaded, and possibly after: presence simply does nothing without it.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Where the MSAL token cache file lives. Under <c>%LOCALAPPDATA%</c> rather than beside
    /// muster.json, because it holds refresh tokens and muster.json is meant to be hand-edited
    /// and copied about.
    /// </summary>
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Muster",
        "msal");

    public string CacheFileName { get; set; } = "muster.msalcache.bin";

    /// <summary>
    /// The public-client redirect URI. Must also be registered on the app registration, as a
    /// "Mobile and desktop applications" redirect URI.
    /// </summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>
    /// Delegated scope for preferred presence. <c>Presence.ReadWrite</c> covers both
    /// <c>setUserPreferredPresence</c> and reading your own presence to check there is a session
    /// to apply it to.
    /// </summary>
    public static IReadOnlyList<string> Scopes { get; } = ["https://graph.microsoft.com/Presence.ReadWrite"];
}
