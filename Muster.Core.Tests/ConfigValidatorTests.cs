using Muster.Core.Config;

namespace Muster.Core.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(ConfigValidator.Validate(ConfigDefaults.Create()));
    }

    [Theory]
    [InlineData(ExternalLinkPolicy.Never)]
    [InlineData(ExternalLinkPolicy.Auto)]
    [InlineData(ExternalLinkPolicy.Always)]
    public void Every_link_policy_the_enum_names_is_accepted(ExternalLinkPolicy policy)
    {
        var config = ConfigDefaults.Create() with { Links = new LinkConfig { External = policy } };

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void An_unknown_link_policy_is_rejected_rather_than_defaulted()
    {
        // Same reasoning as the dom presence strategy: where a link ends up is only visible after
        // you have clicked it, so a policy that silently does nothing is the worst of both.
        var config = ConfigDefaults.Create() with
        {
            Links = new LinkConfig { External = (ExternalLinkPolicy)99 },
        };

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("is not a known policy"));
    }

    [Fact]
    public void Every_icon_set_the_enum_names_is_accepted()
    {
        // Enumerated rather than listed, because the set grows: a member added without artwork
        // behind it should fail somewhere louder than a test nobody remembered to extend.
        foreach (var set in Enum.GetValues<IconSet>())
        {
            var config = ConfigDefaults.Create() with { Appearance = new AppearanceConfig { IconSet = set } };

            Assert.Empty(ConfigValidator.Validate(config));
        }
    }

    [Fact]
    public void An_unknown_icon_set_is_rejected_rather_than_defaulted()
    {
        // Cosmetic, so it could be defaulted away. It is not: the file is hand-edited, and a typo
        // that quietly keeps the old mark reads as the setting being ignored.
        var config = ConfigDefaults.Create() with
        {
            Appearance = new AppearanceConfig { IconSet = (IconSet)99 },
        };

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("is not a known icon set"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("-leading-dash")]
    [InlineData("slash/es")]
    [InlineData("")]
    public void Ids_that_are_unsafe_as_folder_names_are_rejected(string id)
    {
        var config = Config(new WorkspaceConfig { Id = id, Name = "W" });

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("id must be"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOO LONG")]
    public void Abbreviations_that_will_not_fit_the_rail_tile_are_rejected(string abbreviation)
    {
        var config = Config(new WorkspaceConfig { Id = "a", Name = "A", Abbreviation = abbreviation });

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("abbreviation must be"));
    }

    [Fact]
    public void An_omitted_abbreviation_is_fine()
    {
        // Null is the normal case: the tile falls back to initials from the name.
        var config = Config(new WorkspaceConfig { Id = "a", Name = "A", Abbreviation = null });

        Assert.DoesNotContain(ConfigValidator.Validate(config), p => p.Contains("abbreviation"));
    }

    [Fact]
    public void Duplicate_workspace_ids_are_rejected()
    {
        var config = Config(
            new WorkspaceConfig { Id = "dup", Name = "One" },
            new WorkspaceConfig { Id = "dup", Name = "Two" });

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("duplicate workspace id"));
    }

    [Fact]
    public void Service_ids_must_be_unique_across_workspaces()
    {
        // They key the session dictionary and the notification history, so global uniqueness.
        var config = Config(
            new WorkspaceConfig { Id = "a", Name = "A", Services = [Service("teams")] },
            new WorkspaceConfig { Id = "b", Name = "B", Services = [Service("teams")] });

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("duplicate service id"));
    }

    [Fact]
    public void Presence_on_a_generic_service_is_rejected()
    {
        var service = Service("portal") with
        {
            Kind = ServiceKind.Generic,
            Presence = new ServicePresenceConfig { Strategy = PresenceStrategyKind.Graph },
        };

        var problems = ConfigValidator.Validate(Config(
            new WorkspaceConfig { Id = "a", Name = "A", Services = [service] }));

        Assert.Contains(problems, p => p.Contains("teams services only"));
    }

    [Fact]
    public void Graph_strategy_requires_an_account_and_tenant()
    {
        var service = Service("teams") with
        {
            Kind = ServiceKind.Teams,
            Presence = new ServicePresenceConfig { Strategy = PresenceStrategyKind.Graph },
        };

        var problems = ConfigValidator.Validate(Config(
            new WorkspaceConfig { Id = "a", Name = "A", Services = [service] }));

        Assert.Contains(problems, p => p.Contains("userPrincipalName"));
        Assert.Contains(problems, p => p.Contains("tenantId"));
    }

    [Fact]
    public void The_dom_strategy_is_rejected_rather_than_quietly_doing_nothing()
    {
        // Reserved in the schema, not built. Presence failing to apply is invisible by nature, so
        // a config asking for a strategy that does nothing is the worst of both.
        var service = Service("teams") with
        {
            Kind = ServiceKind.Teams,
            Presence = new ServicePresenceConfig { Strategy = PresenceStrategyKind.Dom },
        };

        var problems = ConfigValidator.Validate(Config(
            new WorkspaceConfig { Id = "a", Name = "A", Services = [service] }));

        Assert.Contains(problems, p => p.Contains("'dom' is not implemented"));
    }

    [Fact]
    public void Enabling_presence_with_a_graph_account_requires_a_client_id()
    {
        var config = GraphAccountConfig() with
        {
            Presence = new PresenceConfig { Enabled = true },
        };

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("clientId is required"));
    }

    [Fact]
    public void A_half_configured_account_is_fine_while_presence_is_off()
    {
        // Waiting on a client's admin to consent is a normal state to leave the file in.
        var config = GraphAccountConfig() with
        {
            Presence = new PresenceConfig { Enabled = false },
        };

        Assert.DoesNotContain(ConfigValidator.Validate(config), p => p.Contains("clientId"));
    }

    [Fact]
    public void A_client_id_that_is_not_a_guid_is_rejected()
    {
        var config = GraphAccountConfig() with
        {
            Presence = new PresenceConfig { Enabled = true, ClientId = "the-app" },
        };

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("is not a GUID"));
    }

    [Fact]
    public void A_client_id_is_not_needed_when_no_account_uses_graph()
    {
        var config = ConfigDefaults.Create() with
        {
            Presence = new PresenceConfig { Enabled = true },
        };

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void Blank_expiration_duration_is_rejected()
    {
        // Without it, a crash mid-call leaves Busy stuck across every tenant until noticed.
        var config = ConfigDefaults.Create() with
        {
            Presence = new PresenceConfig { ExpirationDuration = "  " },
        };

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("expirationDuration"));
    }

    [Fact]
    public void A_future_schema_version_is_rejected_rather_than_guessed_at()
    {
        var config = ConfigDefaults.Create() with { Version = MusterConfig.CurrentVersion + 1 };

        Assert.Contains(ConfigValidator.Validate(config), p => p.Contains("newer than this build"));
    }

    [Theory]
    [InlineData("https://portal.azure.com/", true)]
    [InlineData("http://intranet.local/", true)]
    [InlineData("ftp://example.com/", false)]
    [InlineData("file:///c:/temp/", false)]
    public void Service_urls_allow_http_and_https_only(string url, bool valid)
    {
        // Same rule the address bar uses. http stays allowed because intranet sites are real.
        var service = Service("svc") with { Url = new Uri(url) };
        var problems = ConfigValidator.Validate(Config(
            new WorkspaceConfig { Id = "a", Name = "A", Services = [service] }));

        Assert.Equal(valid, !problems.Any(p => p.Contains("url must be")));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(LoggingConfig.MaxRetentionDays, true)]
    [InlineData(-1, false)]
    [InlineData(LoggingConfig.MaxRetentionDays + 1, false)]
    public void Log_retention_must_be_a_sane_number_of_days(int days, bool valid)
    {
        // Zero is legal and means "keep everything". Negative is a mistake, not a shorter window.
        var config = ConfigDefaults.Create() with
        {
            Logging = new LoggingConfig { RetentionDays = days },
        };

        Assert.Equal(valid, !ConfigValidator.Validate(config).Any(p => p.Contains("retentionDays")));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(@"D:\Muster\logs", true)]
    [InlineData(@"logs", false)]
    [InlineData(@"..\logs", false)]
    [InlineData("   ", false)]
    public void A_custom_log_directory_must_be_absolute(string? directory, bool valid)
    {
        // Relative resolves against the working directory, which for a shortcut launch is not
        // anywhere the user would think to look.
        var config = ConfigDefaults.Create() with
        {
            Logging = new LoggingConfig { Directory = directory },
        };

        Assert.Equal(valid, !ConfigValidator.Validate(config).Any(p => p.Contains("logging: directory")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(1440, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1441, false)]
    public void The_idle_window_must_be_a_sane_number_of_minutes(int minutes, bool valid)
    {
        // Zero would suspend a workspace the instant it went off screen, which is indistinguishable
        // from the app being broken. Not suspending at all is what enabled:false is for.
        var config = ConfigDefaults.Create() with
        {
            Suspension = new SuspensionConfig { IdleMinutes = minutes },
        };

        Assert.Equal(valid, !ConfigValidator.Validate(config).Any(p => p.StartsWith("suspension:")));
    }

    /// <summary>One workspace with a fully specified graph presence account in it.</summary>
    private static MusterConfig GraphAccountConfig()
    {
        var service = Service("teams") with
        {
            Kind = ServiceKind.Teams,
            Presence = new ServicePresenceConfig
            {
                Strategy = PresenceStrategyKind.Graph,
                UserPrincipalName = "james@fabrikam.example",
                TenantId = "11111111-1111-1111-1111-111111111111",
            },
        };

        return Config(new WorkspaceConfig { Id = "a", Name = "A", Services = [service] });
    }

    private static MusterConfig Config(params WorkspaceConfig[] workspaces)
        => new() { Workspaces = workspaces };

    private static ServiceConfig Service(string id) => new()
    {
        Id = id,
        Name = id,
        Url = new Uri("https://example.com/"),
    };
}
