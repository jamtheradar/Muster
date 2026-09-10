using Muster.Core.Hosting;
using Muster.Core.Services;

namespace Muster.Core.Tests;

public sealed class NewWindowRouterTests
{
    private static readonly SessionDescriptor Teams =
        Pinned("teams-fabrikam", "https://teams.cloud.microsoft/");

    private static readonly SessionDescriptor Devops =
        Pinned("devops-fabrikam", "https://dev.azure.com/fabrikam/");

    [Fact]
    public void A_link_to_a_pinned_origin_reuses_that_tab()
    {
        var route = NewWindowRouter.Route(
            [Teams, Devops],
            new Uri("https://dev.azure.com/fabrikam/project/_workitems/edit/42"),
            isScriptedPopup: false);

        Assert.Equal(new NewWindowRoute.ActivatePinnedService("devops-fabrikam"), route);
    }

    [Fact]
    public void A_link_to_an_unpinned_origin_opens_an_ephemeral_tab()
    {
        var route = NewWindowRouter.Route(
            [Teams, Devops],
            new Uri("https://example.com/some/article"),
            isScriptedPopup: false);

        Assert.IsType<NewWindowRoute.OpenEphemeralTab>(route);
    }

    [Fact]
    public void A_scripted_popup_never_reuses_a_pinned_tab()
    {
        // Entra sign-in arrives this way. Reusing the Teams tab would break window.opener and
        // throw away the running Teams app.
        var route = NewWindowRouter.Route(
            [Teams],
            new Uri("https://teams.cloud.microsoft/auth/popup"),
            isScriptedPopup: true);

        Assert.IsNotType<NewWindowRoute.ActivatePinnedService>(route);
    }

    [Fact]
    public void A_sized_popup_gets_a_window_of_its_own()
    {
        // A popped-out Teams meeting asks for width and height. Answering with a tab traps it
        // inside the shell, which is exactly what you do not want a meeting to be.
        var route = NewWindowRouter.Route(
            [Teams],
            new Uri("https://teams.cloud.microsoft/meeting/popout"),
            isScriptedPopup: true);

        Assert.IsType<NewWindowRoute.OpenFloatingWindow>(route);
    }

    [Fact]
    public void A_plain_link_still_opens_a_tab_rather_than_a_window()
    {
        // target=_blank is not a request for a window, and a window per link would be unusable.
        var route = NewWindowRouter.Route(
            [Teams],
            new Uri("https://example.com/article"),
            isScriptedPopup: false);

        Assert.IsType<NewWindowRoute.OpenEphemeralTab>(route);
    }

    [Fact]
    public void Nothing_routes_outside_the_app_unless_the_host_says_it_may()
    {
        // A floating window counts as in-app: it is Muster's own WebView2 on the workspace's own
        // profile, and only the shell's window boundary is escaped, never its session isolation.
        // Leaving for the system browser is the one thing that costs the identity, so it happens
        // only when the host has looked at what is registered and said yes.
        var routes = new[]
        {
            NewWindowRouter.Route([Teams], new Uri("https://example.com/"), false),
            NewWindowRouter.Route([Teams], new Uri("https://teams.cloud.microsoft/x"), false),
            NewWindowRouter.Route([], new Uri("https://example.com/"), true),
            NewWindowRouter.Route([Teams], new Uri("https://teams.cloud.microsoft/x"), true),
        };

        Assert.All(routes, route => Assert.True(route
            is NewWindowRoute.ActivatePinnedService
            or NewWindowRoute.OpenEphemeralTab
            or NewWindowRoute.OpenFloatingWindow));
    }

    [Fact]
    public void An_unpinned_link_leaves_the_app_once_the_host_allows_it()
    {
        var route = NewWindowRouter.Route(
            [Teams, Devops],
            new Uri("https://example.com/some/article"),
            isScriptedPopup: false,
            allowExternal: true);

        Assert.IsType<NewWindowRoute.OpenExternally>(route);
    }

    [Fact]
    public void A_pinned_origin_never_leaves_the_app()
    {
        // The tab is already signed in as the right identity. Sending it out to a browser to be
        // signed in again would be slower and, for a tenant with conditional access on, worse.
        var route = NewWindowRouter.Route(
            [Teams, Devops],
            new Uri("https://dev.azure.com/fabrikam/project/_workitems/edit/42"),
            isScriptedPopup: false,
            allowExternal: true);

        Assert.Equal(new NewWindowRoute.ActivatePinnedService("devops-fabrikam"), route);
    }

    [Fact]
    public void A_scripted_popup_never_leaves_the_app()
    {
        // Entra sign-in arrives this way. Handed to another browser it would complete somewhere
        // the page cannot see, leaving the tab waiting on a window.opener that never reports back.
        var route = NewWindowRouter.Route(
            [Teams],
            new Uri("https://login.microsoftonline.com/common/oauth2/authorize"),
            isScriptedPopup: true,
            allowExternal: true);

        Assert.IsType<NewWindowRoute.OpenFloatingWindow>(route);
    }

    [Fact]
    public void A_safe_links_wrapped_link_to_a_pinned_origin_reuses_that_tab()
    {
        // The tenant rewrites every link in a Teams message, so matching what the page handed us
        // means a link to a service pinned in this very workspace matches nothing at all.
        var wrapped = new Uri(
            "https://apc01.safelinks.protection.outlook.com/?url=" +
            "https%3A%2F%2Fdev.azure.com%2Ffabrikam%2Fproject%2F_workitems%2Fedit%2F42&data=05%7C01");

        var route = NewWindowRouter.Route([Teams, Devops], wrapped, isScriptedPopup: false);

        Assert.Equal(new NewWindowRoute.ActivatePinnedService("devops-fabrikam"), route);
    }

    [Fact]
    public void A_safe_links_wrapper_does_not_make_an_unpinned_link_look_pinned()
    {
        var wrapped = new Uri(
            "https://apc01.safelinks.protection.outlook.com/?url=" +
            "https%3A%2F%2Fexample.com%2Farticle&data=05%7C01");

        var route = NewWindowRouter.Route([Teams, Devops], wrapped, isScriptedPopup: false);

        Assert.IsType<NewWindowRoute.OpenEphemeralTab>(route);
    }

    [Fact]
    public void The_first_matching_service_wins()
    {
        var first = Pinned("first", "https://example.com/a");
        var second = Pinned("second", "https://example.com/b");

        var route = NewWindowRouter.Route([first, second], new Uri("https://example.com/c"), false);

        Assert.Equal(new NewWindowRoute.ActivatePinnedService("first"), route);
    }

    [Theory]
    [InlineData("https://example.com/", "https://example.com/other", true)]
    [InlineData("https://example.com/", "https://EXAMPLE.COM/other", true)]
    [InlineData("https://example.com/", "https://sub.example.com/", false)]
    [InlineData("https://example.com/", "http://example.com/", false)]
    [InlineData("https://example.com/", "https://example.com:8443/", false)]
    public void Origin_comparison_is_scheme_host_and_port(string left, string right, bool expected)
        => Assert.Equal(expected, NewWindowRouter.IsSameOrigin(new Uri(left), new Uri(right)));

    [Theory]
    [InlineData("https://www.example.com/x", "example.com")]
    [InlineData("https://portal.azure.com/", "portal.azure.com")]
    public void Ephemeral_tabs_are_labelled_by_host_until_a_title_arrives(string url, string expected)
        => Assert.Equal(expected, SessionDescriptor.DisplayNameFor(new Uri(url)));

    [Fact]
    public void Ephemeral_sessions_run_in_the_workspace_profile()
    {
        var descriptor = SessionDescriptor.ForEphemeral(
            "fabrikam", "ws-fabrikam", new Uri("https://example.com/"));

        Assert.True(descriptor.IsEphemeral);
        Assert.Equal("ws-fabrikam", descriptor.ProfileName);
        Assert.Equal("fabrikam", descriptor.WorkspaceId);
        Assert.StartsWith("eph-", descriptor.Id);
    }

    [Fact]
    public void Every_ephemeral_session_gets_its_own_id()
    {
        var one = SessionDescriptor.ForEphemeral("a", "ws-a", new Uri("https://example.com/"));
        var two = SessionDescriptor.ForEphemeral("a", "ws-a", new Uri("https://example.com/"));

        Assert.NotEqual(one.Id, two.Id);
    }

    private static SessionDescriptor Pinned(string id, string url)
        => new(id, "fabrikam", id, "ws-fabrikam", new Uri(url));
}
