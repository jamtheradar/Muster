namespace Muster.Core.Config;

/// <summary>The config written on first run.</summary>
public static class ConfigDefaults
{
    /// <summary>
    /// One workspace with Teams in it. The id is <c>datebyte</c>, which resolves to the
    /// <c>ws-datebyte</c> profile: that is the profile milestone 1 signed into, and renaming it
    /// would orphan the profile folder and force a fresh sign-in.
    /// </summary>
    public static MusterConfig Create() => new()
    {
        Version = MusterConfig.CurrentVersion,
        Workspaces =
        [
            new WorkspaceConfig
            {
                Id = "datebyte",
                Name = "DataByte",
                Accent = "#2D7D9A",
                Services =
                [
                    new ServiceConfig
                    {
                        Id = "teams-datebyte",
                        Name = "Teams",
                        Kind = ServiceKind.Teams,
                        Url = new Uri("https://teams.cloud.microsoft/"),
                        KeepAlive = true,
                        NotificationsEnabled = true,
                    },
                ],
            },
        ],
        Presence = new PresenceConfig { Enabled = false },
        Notifications = new NotificationConfig(),
        Appearance = new AppearanceConfig(),
        Links = new LinkConfig(),
        Suspension = new SuspensionConfig(),
        Logging = new LoggingConfig(),
    };
}
