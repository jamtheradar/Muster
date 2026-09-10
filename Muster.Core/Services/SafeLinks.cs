namespace Muster.Core.Services;

/// <summary>
/// Peels Microsoft Defender link-protection wrappers off a URL so it can be matched against the
/// services pinned in a workspace.
/// </summary>
/// <remarks>
/// <para>
/// This is load-bearing rather than cosmetic. Where a tenant has Safe Links on, every link in a
/// Teams message is rewritten to
/// <c>https://{tenant}.safelinks.protection.outlook.com/?url={encoded destination}&amp;data=...</c>,
/// so every one of them presents the same host to <see cref="NewWindowRouter"/>. Without this,
/// a link to a service pinned in the very same workspace matches nothing and opens a second copy
/// of it in a throwaway tab, signed in or not.
/// </para>
/// <para>
/// Used for the routing decision only. The URL actually navigated to, or handed to Windows, stays
/// wrapped: Safe Links is a security control the tenant turned on deliberately, and unwrapping it
/// on the way out would quietly opt the user out of the scan. The cost is one redirect through
/// the interstitial, which is what would have happened anyway.
/// </para>
/// </remarks>
public static class SafeLinks
{
    /// <summary>
    /// A wrapped link can be wrapped again — forwarded mail through two tenants does it — but a
    /// chain longer than this is a redirect loop, not a click worth following.
    /// </summary>
    private const int MaxUnwraps = 5;

    /// <summary>The query parameter carrying the real destination, for every wrapper family below.</summary>
    /// <remarks>
    /// The Teams interstitial also carries <c>dest</c>, which is the reputation service it calls
    /// rather than where the click is going. Reading that one would route every link to Microsoft.
    /// </remarks>
    private const string DestinationParameter = "url";

    /// <summary>CDN hosts observed serving the Teams Safe Links interstitial.</summary>
    private static readonly HashSet<string> TeamsInterstitialHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "statics.teams.cdn.office.net",
            "teams.public.onecdn.static.microsoft",
        };

    /// <summary>
    /// The destination behind any link-protection wrappers, or <paramref name="url"/> itself when
    /// there are none.
    /// </summary>
    public static Uri Unwrap(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var current = url;

        for (var i = 0; i < MaxUnwraps; i++)
        {
            if (TryUnwrapOnce(current) is not { } inner)
            {
                break;
            }

            current = inner;
        }

        return current;
    }

    /// <summary>
    /// The destination, and whether unwrapping it changed anything. For logging, where saying
    /// only the wrapper host makes every routing decision in the file look identical.
    /// </summary>
    public static bool TryUnwrap(Uri url, out Uri destination)
    {
        destination = Unwrap(url);
        return destination != url;
    }

    /// <summary>
    /// Known link-protection wrappers. Deliberately limited to the Microsoft families Outlook and
    /// Teams actually produce: a general-purpose redirect follower would unwrap tracking links
    /// nobody asked it to touch.
    /// </summary>
    public static bool IsWrapper(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        // Regional prefixes: apc01.safelinks.protection.outlook.com, nam02..., and so on.
        if (uri.Host.EndsWith(".safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Teams renders its own Safe Links interstitial from a CDN rather than from the safelinks
        // host, so matching only the host above misses every link clicked in a chat. Which CDN is
        // not stable: the host observed in this deployment on 2026-09-02 was
        // teams.public.onecdn.static.microsoft, serving /evergreen-assets/safelinks/2/
        // atp-safelinks.html, where the documented one is statics.teams.cdn.office.net. Expect to
        // add to this list; a link that turns up as an ephemeral tab named after a CDN host is
        // what it looks like when one is missing.
        return TeamsInterstitialHosts.Contains(uri.Host)
            && uri.AbsolutePath.Contains("safelinks", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? TryUnwrapOnce(Uri uri)
    {
        if (!IsWrapper(uri))
        {
            return null;
        }

        if (ReadQueryParameter(uri.Query, DestinationParameter) is not { } destination)
        {
            return null;
        }

        // Decoded once. A doubly wrapped link is handled by the caller's loop rather than by
        // decoding repeatedly here, which would corrupt a URL legitimately containing an encoded
        // per-cent sign.
        var decoded = Uri.UnescapeDataString(destination);

        // Anything but an absolute http(s) destination means the wrapper was misread, in which
        // case the original URL is the safer answer than a guess at what it meant.
        return Uri.TryCreate(decoded, UriKind.Absolute, out var inner)
            && UrlNormaliser.IsSupportedScheme(inner)
                ? inner
                : null;
    }

    /// <summary>
    /// Minimal query reader. <c>+</c> is left alone: Safe Links per-cent encodes everything, and
    /// treating it as a space corrupts destinations that legitimately contain one.
    /// </summary>
    private static string? ReadQueryParameter(string query, string name)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        var remaining = query.AsSpan(query[0] == '?' ? 1 : 0);

        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOf('&');
            var pair = separator < 0 ? remaining : remaining[..separator];
            remaining = separator < 0 ? default : remaining[(separator + 1)..];

            var equals = pair.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            if (pair[..equals].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair[(equals + 1)..].ToString();
            }
        }

        return null;
    }
}
