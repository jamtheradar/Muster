using Muster.Core.Config;

namespace Muster.Core.Tests;

/// <summary>
/// Hot-reload's whole justification is that editing the config does not have to cost you five
/// signed-in tenants. These tests are where that promise is kept: anything that wrongly lands in
/// <see cref="ConfigDiff.RecreatedServiceIds"/> is a session torn down for no reason, and anything
/// wrongly left out of it is a session running with settings it cannot actually honour.
/// </summary>
public sealed class ConfigDiffTests
{
    [Fact]
    public void Identical_configs_produce_an_empty_diff()
    {
        var config = Sample();

        var diff = ConfigDiff.Between(config, config);

        Assert.True(diff.IsEmpty);
        Assert.False(diff.RequiresRestart);
        Assert.Equal(ConfigDiff.None, diff);
        Assert.Empty(diff.StoppedServiceIds);
    }

    [Fact]
    public void Changing_the_link_policy_costs_no_session()
    {
        // Read per click rather than held anywhere, so it is live the moment the file lands.
        var before = Sample();
        var after = before with { Links = new LinkConfig { External = ExternalLinkPolicy.Never } };

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.LinksChanged);
        Assert.False(diff.IsEmpty);
        Assert.False(diff.RequiresRestart);
        Assert.Empty(diff.StoppedServiceIds);
        Assert.Empty(diff.NavigatedServiceIds);
    }

    [Fact]
    public void A_link_policy_that_did_not_change_is_not_reported()
    {
        var before = Sample() with { Links = new LinkConfig { External = ExternalLinkPolicy.Always } };
        var after = before with { Links = new LinkConfig { External = ExternalLinkPolicy.Always } };

        Assert.False(ConfigDiff.Between(before, after).LinksChanged);
    }

    [Fact]
    public void Changing_the_icon_set_costs_no_session_and_no_restart()
    {
        // Every surface it reaches can be repainted underneath a running shell, which is the only
        // reason a cosmetic setting is hot-reloaded at all.
        var before = Sample();
        var after = before with { Appearance = new AppearanceConfig { IconSet = IconSet.Eucalypt } };

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.AppearanceChanged);
        Assert.False(diff.IsEmpty);
        Assert.False(diff.RequiresRestart);
        Assert.Empty(diff.StoppedServiceIds);
        Assert.Empty(diff.NavigatedServiceIds);
    }

    [Fact]
    public void An_icon_set_that_did_not_change_is_not_reported()
    {
        var before = Sample() with { Appearance = new AppearanceConfig { IconSet = IconSet.Inverted } };
        var after = before with { Appearance = new AppearanceConfig { IconSet = IconSet.Inverted } };

        Assert.False(ConfigDiff.Between(before, after).AppearanceChanged);
    }

    // ---- the cheap changes: nothing may be torn down --------------------------------------

    [Theory]
    [InlineData("name")]
    [InlineData("accent")]
    [InlineData("abbreviation")]
    public void Workspace_cosmetics_disturb_no_session(string field)
    {
        var before = Sample();
        var workspace = before.Workspaces[0];

        var after = With(before, 0, field switch
        {
            "name" => workspace with { Name = "Renamed" },
            "accent" => workspace with { Accent = "#112233" },
            _ => workspace with { Abbreviation = "ZZ" },
        });

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.WorkspacesChanged);
        Assert.Empty(diff.StoppedServiceIds);
        Assert.Empty(diff.NavigatedServiceIds);
        Assert.False(diff.RequiresRestart);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("muted")]
    [InlineData("notifications")]
    [InlineData("keepAlive")]
    [InlineData("kind")]
    public void Service_flags_disturb_no_session(string field)
    {
        var before = Sample();
        var service = before.Workspaces[0].Services[0];

        var after = WithService(before, 0, 0, field switch
        {
            "name" => service with { Name = "Renamed" },
            "muted" => service with { Muted = true },
            "notifications" => service with { NotificationsEnabled = false },
            "keepAlive" => service with { KeepAlive = false },
            _ => service with { Kind = ServiceKind.Generic },
        });

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.WorkspacesChanged);
        Assert.Empty(diff.StoppedServiceIds);
        Assert.Empty(diff.NavigatedServiceIds);
    }

    [Fact]
    public void A_changed_url_is_a_navigation_not_a_teardown()
    {
        // The point of the whole exercise: a URL edit must not sign the tenant out.
        var before = Sample();
        var service = before.Workspaces[0].Services[0];
        var after = WithService(before, 0, 0, service with { Url = new Uri("https://teams.cloud.microsoft/v2/") });

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["teams-db"], diff.NavigatedServiceIds);
        Assert.Empty(diff.StoppedServiceIds);
    }

    // ---- the expensive changes: the session cannot survive them ----------------------------

    [Fact]
    public void A_changed_service_profile_recreates_that_session_only()
    {
        var before = Sample();
        var service = before.Workspaces[0].Services[0];
        var after = WithService(before, 0, 0, service with { ProfileName = "ws-db-alt" });

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["teams-db"], diff.RecreatedServiceIds);
        Assert.Empty(diff.NavigatedServiceIds);
        Assert.Empty(diff.RemovedServiceIds);
    }

    [Fact]
    public void A_changed_workspace_profile_recreates_every_service_that_inherits_it()
    {
        // Both services in the workspace resolve their profile from the workspace, so both go.
        var before = Sample();
        var after = With(before, 0, before.Workspaces[0] with { ProfileName = "ws-db-2" });

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["teams-db", "wiki-db"], diff.RecreatedServiceIds);
    }

    [Fact]
    public void A_service_pinned_to_its_own_profile_survives_a_workspace_profile_change()
    {
        var before = WithService(
            Sample(),
            0,
            1,
            Sample().Workspaces[0].Services[1] with { ProfileName = "pinned-elsewhere" });

        var after = With(before, 0, before.Workspaces[0] with { ProfileName = "ws-db-2" });

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["teams-db"], diff.RecreatedServiceIds);
    }

    [Fact]
    public void Changed_permission_origins_recreate_the_workspace_sessions()
    {
        // The allow list is read off the descriptor the session was built with, so it cannot be
        // changed underneath a running control.
        var before = Sample();
        var after = With(before, 0, before.Workspaces[0] with { AllowedPermissionOrigins = ["contoso.com"] });

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["teams-db", "wiki-db"], diff.RecreatedServiceIds);
    }

    [Fact]
    public void Reordering_permission_origins_is_not_a_change()
    {
        var before = With(
            Sample(),
            0,
            Sample().Workspaces[0] with { AllowedPermissionOrigins = ["a.com", "b.com"] });

        var after = With(before, 0, before.Workspaces[0] with { AllowedPermissionOrigins = ["a.com", "b.com"] });

        Assert.True(ConfigDiff.Between(before, after).IsEmpty);
    }

    [Fact]
    public void A_service_moved_between_workspaces_is_recreated()
    {
        var before = Sample();
        var moved = before.Workspaces[0].Services[1];

        var after = before with
        {
            Workspaces =
            [
                before.Workspaces[0] with { Services = [before.Workspaces[0].Services[0]] },
                before.Workspaces[1] with { Services = [.. before.Workspaces[1].Services, moved] },
            ],
        };

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["wiki-db"], diff.RecreatedServiceIds);
        Assert.Empty(diff.RemovedServiceIds);
        Assert.Empty(diff.AddedServiceIds);
    }

    // ---- adding and removing ----------------------------------------------------------------

    [Fact]
    public void An_added_service_is_added_and_nothing_else_moves()
    {
        var before = Sample();
        var after = With(before, 0, before.Workspaces[0] with
        {
            Services = [.. before.Workspaces[0].Services, Service("crm-db", "https://crm.example/")],
        });

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["crm-db"], diff.AddedServiceIds);
        Assert.Empty(diff.StoppedServiceIds);
    }

    [Fact]
    public void A_removed_service_stops_its_session()
    {
        var before = Sample();
        var after = With(before, 0, before.Workspaces[0] with { Services = [before.Workspaces[0].Services[0]] });

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["wiki-db"], diff.RemovedServiceIds);
        Assert.Equal(["wiki-db"], diff.StoppedServiceIds);
        Assert.Empty(diff.AddedServiceIds);
    }

    [Fact]
    public void A_removed_workspace_stops_all_of_its_sessions()
    {
        var before = Sample();
        var after = before with { Workspaces = [before.Workspaces[0]] };

        var diff = ConfigDiff.Between(before, after);

        Assert.Equal(["teams-client"], diff.RemovedServiceIds);
        Assert.True(diff.WorkspacesChanged);
    }

    [Fact]
    public void Reordering_workspaces_moves_the_rail_and_nothing_else()
    {
        var before = Sample();
        var after = before with { Workspaces = [before.Workspaces[1], before.Workspaces[0]] };

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.WorkspacesChanged);
        Assert.Empty(diff.StoppedServiceIds);
        Assert.Empty(diff.AddedServiceIds);
        Assert.Empty(diff.NavigatedServiceIds);
    }

    // ---- the sections that are not workspaces ------------------------------------------------

    [Fact]
    public void Presence_and_notification_settings_change_without_touching_sessions()
    {
        var before = Sample();
        var after = before with
        {
            Presence = before.Presence with { CallClearDelaySeconds = 20 },
            Notifications = before.Notifications with { ToastsEnabled = false, HistoryLimit = 50 },
        };

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.PresenceChanged);
        Assert.True(diff.NotificationsChanged);
        Assert.False(diff.WorkspacesChanged);
        Assert.Empty(diff.StoppedServiceIds);
        Assert.False(diff.RequiresRestart);
    }

    [Fact]
    public void Suspension_settings_change_without_touching_sessions()
    {
        // Suspension is decided fresh on every tick from the descriptor and the live config, so
        // nothing about it is baked into a session at creation time. Tearing one down to change
        // an idle window would be the exact cost hot-reload exists to avoid.
        var before = Sample();
        var after = before with
        {
            Suspension = before.Suspension with { Enabled = false, IdleMinutes = 30 },
        };

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.SuspensionChanged);
        Assert.False(diff.IsEmpty);
        Assert.False(diff.WorkspacesChanged);
        Assert.Empty(diff.StoppedServiceIds);
        Assert.False(diff.RequiresRestart);
    }

    [Fact]
    public void Log_level_and_retention_apply_live()
    {
        var before = Sample();
        var after = before with
        {
            Logging = before.Logging with { Level = LogVerbosity.Trace, RetentionDays = 3 },
        };

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.LoggingChanged);
        Assert.False(diff.RequiresRestart);
    }

    [Fact]
    public void Moving_the_log_folder_needs_a_restart()
    {
        var before = Sample();
        var after = before with { Logging = before.Logging with { Directory = @"D:\elsewhere" } };

        var diff = ConfigDiff.Between(before, after);

        Assert.True(diff.RequiresRestart);
        Assert.Contains("log folder", diff.RestartReasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_version_change_needs_a_restart()
    {
        var before = Sample();

        var diff = ConfigDiff.Between(before, before with { Version = before.Version + 1 });

        Assert.True(diff.RequiresRestart);
        Assert.False(diff.WorkspacesChanged);
        Assert.Empty(diff.StoppedServiceIds);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static MusterConfig Sample() => new()
    {
        Workspaces =
        [
            new WorkspaceConfig
            {
                Id = "db",
                Name = "DataByte",
                Accent = "#2D7D9A",
                Services =
                [
                    Service("teams-db", "https://teams.cloud.microsoft/", ServiceKind.Teams, keepAlive: true),
                    Service("wiki-db", "https://wiki.example/"),
                ],
            },
            new WorkspaceConfig
            {
                Id = "client",
                Name = "Client One",
                Accent = "#8A4B2D",
                Services = [Service("teams-client", "https://teams.cloud.microsoft/", ServiceKind.Teams, keepAlive: true)],
            },
        ],
    };

    private static ServiceConfig Service(
        string id,
        string url,
        ServiceKind kind = ServiceKind.Generic,
        bool keepAlive = false) => new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            Url = new Uri(url),
            KeepAlive = keepAlive,
        };

    private static MusterConfig With(MusterConfig config, int index, WorkspaceConfig workspace)
    {
        var workspaces = config.Workspaces.ToList();
        workspaces[index] = workspace;
        return config with { Workspaces = workspaces };
    }

    private static MusterConfig WithService(
        MusterConfig config,
        int workspaceIndex,
        int serviceIndex,
        ServiceConfig service)
    {
        var workspace = config.Workspaces[workspaceIndex];
        var services = workspace.Services.ToList();
        services[serviceIndex] = service;
        return With(config, workspaceIndex, workspace with { Services = services });
    }
}
