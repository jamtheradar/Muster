using Muster.Core.Config;
using Muster.Core.Hosting;

namespace Muster.Core.Tests;

/// <summary>
/// Profile names decide which cookies and auth state a session sees. Getting one wrong puts a
/// tenant in the wrong identity, which is the failure this whole app exists to prevent.
/// </summary>
public sealed class ProfileResolutionTests
{
    [Fact]
    public void Workspace_profile_defaults_to_ws_prefix()
    {
        var workspace = Workspace("fabrikam");

        Assert.Equal("ws-fabrikam", workspace.ResolvedProfileName);
    }

    [Fact]
    public void Workspace_profile_override_wins()
    {
        var workspace = Workspace("fabrikam") with { ProfileName = "legacy-fabrikam" };

        Assert.Equal("legacy-fabrikam", workspace.ResolvedProfileName);
    }

    [Fact]
    public void Service_inherits_the_workspace_profile()
    {
        var workspace = Workspace("fabrikam");
        var service = Service("teams-fabrikam");

        Assert.Equal("ws-fabrikam", service.ResolveProfileName(workspace));
    }

    [Fact]
    public void Service_profile_override_wins()
    {
        // The rare case: two identities inside one tenant.
        var workspace = Workspace("fabrikam");
        var service = Service("teams-fabrikam-admin") with { ProfileName = "ws-fabrikam-admin" };

        Assert.Equal("ws-fabrikam-admin", service.ResolveProfileName(workspace));
    }

    [Fact]
    public void Descriptor_carries_the_resolved_profile_and_workspace()
    {
        var workspace = Workspace("fabrikam");
        var service = Service("teams-fabrikam");

        var descriptor = SessionDescriptor.ForService(workspace, service);

        Assert.Equal("teams-fabrikam", descriptor.Id);
        Assert.Equal("fabrikam", descriptor.WorkspaceId);
        Assert.Equal("ws-fabrikam", descriptor.ProfileName);
        Assert.Equal(service.Url, descriptor.Home);
    }

    [Fact]
    public void Two_workspaces_never_share_a_profile()
    {
        Assert.NotEqual(Workspace("a").ResolvedProfileName, Workspace("b").ResolvedProfileName);
    }

    private static WorkspaceConfig Workspace(string id) => new() { Id = id, Name = id };

    private static ServiceConfig Service(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = ServiceKind.Teams,
        Url = new Uri("https://teams.cloud.microsoft/"),
    };
}
