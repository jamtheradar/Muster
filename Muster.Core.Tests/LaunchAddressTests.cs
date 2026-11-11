using Muster.Core.Hosting;

namespace Muster.Core.Tests;

/// <summary>
/// The pattern is hand-written and the output is whatever the process feels like printing, so
/// what gets accepted as the address is worth pinning down. The host check is the one that
/// matters: without it, a loose pattern sends the tab wherever the first link in the output goes.
/// </summary>
public sealed class LaunchAddressTests
{
    private static readonly Uri Expected = new("http://127.0.0.1/");

    [Fact]
    public void The_first_group_is_the_address()
    {
        var pattern = LaunchAddress.Compile(@"policy GUI: (http\S+)");

        Assert.True(LaunchAddress.TryRead(
            pattern,
            "CC Safety Net policy GUI: http://127.0.0.1:64050/?token=abc",
            Expected,
            out var address));
        Assert.Equal(new Uri("http://127.0.0.1:64050/?token=abc"), address);
    }

    [Fact]
    public void A_line_that_does_not_match_is_ignored()
    {
        var pattern = LaunchAddress.Compile(@"policy GUI: (http\S+)");

        Assert.False(LaunchAddress.TryRead(pattern, "npm warn exec The following package was not found", Expected, out _));
        Assert.False(LaunchAddress.TryRead(pattern, null, Expected, out _));
        Assert.False(LaunchAddress.TryRead(pattern, string.Empty, Expected, out _));
    }

    [Fact]
    public void An_address_on_another_host_is_refused()
    {
        // The shape of the failure: an update notice printed before the server is up.
        var pattern = LaunchAddress.Compile(@"(https?://\S+)");

        Assert.False(LaunchAddress.TryRead(
            pattern,
            "New version available: https://github.com/example/releases",
            Expected,
            out _));
    }

    [Fact]
    public void The_port_is_left_to_the_process()
    {
        var pattern = LaunchAddress.Compile(@"(http\S+)");

        Assert.True(LaunchAddress.TryRead(pattern, "http://127.0.0.1:5173/", Expected, out _));
        Assert.True(LaunchAddress.TryRead(pattern, "http://127.0.0.1:1/", Expected, out _));
    }

    [Fact]
    public void Something_that_is_not_an_http_address_is_refused()
    {
        var pattern = LaunchAddress.Compile(@"at (\S+)");

        Assert.False(LaunchAddress.TryRead(pattern, @"at C:\tools\server.exe", Expected, out _));
        Assert.False(LaunchAddress.TryRead(pattern, "at file://127.0.0.1/share", Expected, out _));
    }

    [Fact]
    public void A_group_that_did_not_take_part_is_ignored()
    {
        var pattern = LaunchAddress.Compile(@"ready(?: at (http\S+))?");

        Assert.False(LaunchAddress.TryRead(pattern, "ready", Expected, out _));
    }

    [Fact]
    public void A_catastrophic_pattern_fails_the_line_instead_of_hanging()
    {
        var pattern = LaunchAddress.Compile(@"^(a+)+$");

        Assert.False(LaunchAddress.TryRead(pattern, new string('a', 40) + "!", Expected, out _));
    }

    [Fact]
    public void The_logged_form_drops_the_query()
    {
        // Where local servers keep their access token.
        Assert.Equal(
            "http://127.0.0.1:64050/",
            LaunchAddress.Redacted(new Uri("http://127.0.0.1:64050/?token=secret")));
    }
}
