using Muster.Core.Services;

namespace Muster.Core.Tests;

public sealed class UrlNormaliserTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  example.com  ", "https://example.com/")]
    [InlineData("example.com/a/b?c=d", "https://example.com/a/b?c=d")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("http://internal.example/", "http://internal.example/")]
    [InlineData("localhost:5000", "https://localhost:5000/")]
    public void Accepts_and_normalises(string input, string expected)
    {
        Assert.True(UrlNormaliser.TryNormalise(input, out var result));
        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("just some words")]
    [InlineData("teams")]
    public void Rejects_input_that_is_not_a_url(string? input)
        => Assert.False(UrlNormaliser.TryNormalise(input, out _));

    [Theory]
    [InlineData("file:///c:/windows/system32/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/")]
    public void Rejects_non_http_schemes(string input)
        => Assert.False(UrlNormaliser.TryNormalise(input, out _));

    [Fact]
    public void Bare_input_defaults_to_https()
    {
        Assert.True(UrlNormaliser.TryNormalise("portal.azure.com", out var result));
        Assert.Equal(Uri.UriSchemeHttps, result.Scheme);
    }
}
