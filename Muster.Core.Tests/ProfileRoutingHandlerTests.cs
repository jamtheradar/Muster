using Muster.Core.Services;

namespace Muster.Core.Tests;

/// <summary>
/// The check behind <c>links.external: auto</c>. Saying yes wrongly means a link opening in the
/// wrong signed-in account, which is the failure the whole app exists to prevent, so these lean
/// hard on the cases where the answer must be no.
/// </summary>
public sealed class ProfileRoutingHandlerTests
{
    [Theory]
    [InlineData("UrlRouterURL")]
    [InlineData("DataByteUrlRouterURL")]
    [InlineData("urlrouterurl")]
    public void A_known_progid_is_enough_on_its_own(string progId)
        => Assert.True(ProfileRoutingHandler.Matches(progId, openCommand: null));

    [Fact]
    public void A_branded_build_is_recognised_by_its_executable()
    {
        // The installed build and the upstream source register different ProgIds, so a single
        // hardcoded name was wrong on the first machine this met. The executable survives the
        // rebrand.
        var recognised = ProfileRoutingHandler.Matches(
            "SomeForkURL",
            @"""C:\Users\someone\AppData\Local\DataByte\UrlRouter\UrlRouter.exe"" --single-argument %1");

        Assert.True(recognised);
    }

    [Theory]
    [InlineData("MSEdgeHTM", @"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"" ""%1""")]
    [InlineData("ChromeHTML", @"""C:\Program Files\Google\Chrome\Application\chrome.exe"" -- ""%1""")]
    [InlineData("FirefoxURL-308046B0AF4A39CB", @"""C:\Program Files\Mozilla Firefox\firefox.exe"" -osint -url ""%1""")]
    public void An_ordinary_browser_is_never_a_profile_router(string progId, string command)
        => Assert.False(ProfileRoutingHandler.Matches(progId, command));

    [Fact]
    public void Knowing_nothing_is_answered_with_no()
    {
        // A managed machine can have no UserChoice at all. Under Auto that has to mean "keep the
        // link inside Muster", never "assume the best".
        Assert.False(ProfileRoutingHandler.Matches(null, null));
        Assert.False(ProfileRoutingHandler.Matches(null, "   "));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Thing\UrlRouter.exe"" --single-argument %1", "UrlRouter.exe")]
    [InlineData(@"C:\Tools\UrlRouter.exe %1", "UrlRouter.exe")]
    [InlineData(@"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"" ""%1""", "msedge.exe")]
    public void The_executable_is_picked_out_of_the_command(string command, string expected)
        => Assert.Equal(expected, ProfileRoutingHandler.ExecutableOf(command));

    [Fact]
    public void An_unquoted_path_with_spaces_is_not_mistaken_for_a_match()
    {
        // Taking everything up to the first space would give "C:\Program", which matches nothing.
        // The point of this test is that it must not accidentally match either.
        Assert.Equal("Program", ProfileRoutingHandler.ExecutableOf(@"C:\Program Files\Thing\UrlRouter.exe %1"));
        Assert.False(ProfileRoutingHandler.Matches(null, @"C:\Program Files\Thing\UrlRouter.exe %1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated quote")]
    public void A_command_that_cannot_be_read_yields_nothing(string? command)
        => Assert.Null(ProfileRoutingHandler.ExecutableOf(command));
}
