using Muster.Core.Services;

namespace Muster.Core.Tests;

/// <summary>
/// Safe Links makes every link in a Teams message wear the same host. Getting this wrong is not
/// visible as a failure — it shows up as a second copy of a service you already have pinned,
/// opening in a tab that is not signed in.
/// </summary>
public sealed class SafeLinksTests
{
    [Fact]
    public void An_ordinary_url_is_returned_untouched()
    {
        var url = new Uri("https://dev.azure.com/fabrikam/project?a=1&b=2");

        Assert.Equal(url, SafeLinks.Unwrap(url));
        Assert.False(SafeLinks.TryUnwrap(url, out _));
    }

    [Theory]
    [InlineData("https://apc01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com%2Fa")]
    [InlineData("https://nam02.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com%2Fa")]
    [InlineData("https://safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com%2Fa")]
    public void Every_regional_safelinks_host_is_unwrapped(string wrapped)
        => Assert.Equal(new Uri("https://example.com/a"), SafeLinks.Unwrap(new Uri(wrapped)));

    [Fact]
    public void The_teams_interstitial_on_the_office_cdn_is_unwrapped_too()
    {
        // Teams renders its own Safe Links page from the CDN rather than from the safelinks host,
        // so matching only the host above misses every link clicked in a chat.
        var wrapped = new Uri(
            "https://statics.teams.cdn.office.net/evergreen-assets/safelinks/1/atp-safelinks.html" +
            "?url=https%3A%2F%2Fexample.com%2Fa");

        Assert.Equal(new Uri("https://example.com/a"), SafeLinks.Unwrap(wrapped));
    }

    [Fact]
    public void The_interstitial_host_this_tenant_actually_uses_is_unwrapped()
    {
        // Captured from the log on 2026-09-02 08:47, clicking a Jira link in a Teams message. The
        // host is not the documented statics.teams.cdn.office.net, which is exactly why this is
        // pinned by a real example rather than by the one the documentation names. Trimmed: the
        // real query carries about 3KB of pc, sd and clickparams as well.
        var wrapped = new Uri(
            "https://teams.public.onecdn.static.microsoft/evergreen-assets/safelinks/2/atp-safelinks.html" +
            "?url=https%3A%2F%2Ffabrikamltd.atlassian.net%2Fbrowse%2FPROJ-1234" +
            "&locale=en-us" +
            "&dest=https%3A%2F%2Fteams.cloud.microsoft%2Fapi%2Fmt%2Fapac%2Fbeta%2Fatpsafelinks%2F" +
            "&ce=prod&ring=general");

        Assert.Equal(new Uri("https://fabrikamltd.atlassian.net/browse/PROJ-1234"), SafeLinks.Unwrap(wrapped));
    }

    [Fact]
    public void The_interstitials_dest_parameter_is_not_mistaken_for_the_destination()
    {
        // dest is the reputation service the interstitial calls, not where the click is going.
        // Reading it would route every link in every Teams message to Microsoft.
        var wrapped = new Uri(
            "https://teams.public.onecdn.static.microsoft/evergreen-assets/safelinks/2/atp-safelinks.html" +
            "?dest=https%3A%2F%2Fteams.cloud.microsoft%2Fapi%2Fmt%2Fapac%2Fbeta%2Fatpsafelinks%2F" +
            "&url=https%3A%2F%2Fexample.com%2Fa");

        Assert.Equal(new Uri("https://example.com/a"), SafeLinks.Unwrap(wrapped));
    }

    [Fact]
    public void A_page_on_the_office_cdn_that_is_not_the_interstitial_is_left_alone()
    {
        var url = new Uri("https://statics.teams.cdn.office.net/evergreen-assets/icon.png?url=x");

        Assert.Equal(url, SafeLinks.Unwrap(url));
    }

    [Fact]
    public void The_rest_of_the_wrappers_query_string_is_ignored()
    {
        // Real wrappers carry data, sdata and reserved alongside the destination.
        var wrapped = new Uri(
            "https://apc01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com%2Fa%3Fq%3D1" +
            "&data=05%7C01%7C&sdata=abc%3D&reserved=0");

        Assert.Equal(new Uri("https://example.com/a?q=1"), SafeLinks.Unwrap(wrapped));
    }

    [Fact]
    public void A_doubly_wrapped_link_unwraps_all_the_way()
    {
        // Mail forwarded between two tenants is rewritten twice.
        var inner = Uri.EscapeDataString("https://example.com/a");
        var middle = Uri.EscapeDataString($"https://nam02.safelinks.protection.outlook.com/?url={inner}");
        var wrapped = new Uri($"https://apc01.safelinks.protection.outlook.com/?url={middle}");

        Assert.Equal(new Uri("https://example.com/a"), SafeLinks.Unwrap(wrapped));
    }

    [Fact]
    public void A_wrapper_with_no_destination_is_left_alone()
    {
        // Misreading the wrapper means the original URL is the safer answer than a guess.
        var wrapped = new Uri("https://apc01.safelinks.protection.outlook.com/?data=05%7C01");

        Assert.Equal(wrapped, SafeLinks.Unwrap(wrapped));
    }

    [Fact]
    public void A_destination_that_is_not_http_is_refused()
    {
        // The same rule as the address bar: a wrapper is not a way to reach file: or javascript:.
        var wrapped = new Uri(
            "https://apc01.safelinks.protection.outlook.com/?url=" +
            Uri.EscapeDataString(@"file:///C:/Windows/System32/calc.exe"));

        Assert.Equal(wrapped, SafeLinks.Unwrap(wrapped));
    }

    [Fact]
    public void A_lookalike_host_is_not_a_wrapper()
    {
        // The suffix check is anchored on a dot, so this must not match.
        var url = new Uri("https://safelinks.protection.outlook.com.attacker.test/?url=https%3A%2F%2Fx.test%2F");

        Assert.False(SafeLinks.IsWrapper(url));
        Assert.Equal(url, SafeLinks.Unwrap(url));
    }

    [Fact]
    public void A_destination_containing_an_encoded_per_cent_survives()
    {
        // Decoded once, not repeatedly: decoding twice turns %2520 into a space and corrupts the URL.
        var wrapped = new Uri(
            "https://apc01.safelinks.protection.outlook.com/?url=" +
            Uri.EscapeDataString("https://example.com/a%20b"));

        Assert.Equal(new Uri("https://example.com/a%20b"), SafeLinks.Unwrap(wrapped));
    }
}
