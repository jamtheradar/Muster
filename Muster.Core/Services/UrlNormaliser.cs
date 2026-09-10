namespace Muster.Core.Services;

/// <summary>Turns what a human types in the address bar into a URL worth navigating to.</summary>
public static class UrlNormaliser
{
    /// <summary>
    /// Accepts <c>example.com</c>, <c>example.com/path</c> and full URLs. Bare input gets
    /// <c>https://</c>, never <c>http://</c>. Anything that is not http(s) is rejected rather
    /// than guessed at, so a typo cannot become a shell or file URL.
    /// </summary>
    public static bool TryNormalise(string? input, out Uri result)
    {
        result = null!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            // A bare word with no dot is more likely a mistake than a hostname. There is no
            // search provider here by design, so reject it rather than inventing one.
            if (!trimmed.Contains('.', StringComparison.Ordinal)
                && !trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!IsSupportedScheme(parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    /// <summary>
    /// The only schemes Muster will load, wherever a URL comes from: the address bar, a link, or
    /// muster.json. http is allowed because intranet sites are real; everything beyond http(s) is
    /// rejected so a typo cannot become a file: or javascript: navigation.
    /// </summary>
    public static bool IsSupportedScheme(Uri uri)
        => uri.IsAbsoluteUri
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
