using System.Text.Json;
using Muster.Core.Config;

namespace Muster.Core.Tests;

/// <summary>
/// The config file is hand-edited, so the shape written by the app and the shape a human types
/// must be the same shape. The golden file is the contract.
/// </summary>
public sealed class ConfigRoundTripTests
{
    private static readonly string GoldenPath = Path.Combine(
        AppContext.BaseDirectory, "Golden", "muster.golden.json");

    [Fact]
    public async Task Golden_file_loads()
    {
        var config = await LoadGoldenAsync();

        Assert.Equal(1, config.Version);
        Assert.Equal(2, config.Workspaces.Count);
        Assert.Equal("DataByte", config.Workspaces[0].Name);
        Assert.Equal(2, config.Workspaces[0].Services.Count);
        Assert.True(config.Presence.Enabled);
        Assert.Equal("PT2H", config.Presence.ExpirationDuration);
        Assert.Equal("22222222-2222-2222-2222-222222222222", config.Presence.ClientId);
        Assert.Equal(200, config.Notifications.HistoryLimit);
        Assert.Equal(IconSet.Eucalypt, config.Appearance.IconSet);
        Assert.Equal(ExternalLinkPolicy.Always, config.Links.External);
        Assert.Equal(LogVerbosity.Debug, config.Logging.Level);
        Assert.Equal(30, config.Logging.RetentionDays);
        Assert.Equal(@"D:\Muster\logs", config.Logging.Directory);
    }

    [Fact]
    public void Defaults_write_a_logging_section()
    {
        // It is a record with non-null defaults, so it is always written. That is deliberate: the
        // file is hand-edited, and an option you cannot see is an option you will not find.
        var logging = ConfigDefaults.Create().Logging;

        Assert.Equal(LogVerbosity.Information, logging.Level);
        Assert.Equal(LoggingConfig.DefaultRetentionDays, logging.RetentionDays);
        Assert.Null(logging.Directory);
    }

    [Fact]
    public void Defaults_write_a_links_section()
    {
        // Always written, for the same reason logging is: the file is hand-edited, and an option
        // you cannot see is an option you will not find.
        var links = ConfigDefaults.Create().Links;

        Assert.Equal(ExternalLinkPolicy.Auto, links.External);
    }

    [Fact]
    public void Defaults_write_an_appearance_section()
    {
        // Always written, for the same reason logging and links are: the file is hand-edited, and
        // an option you cannot see is an option you will not find.
        var appearance = ConfigDefaults.Create().Appearance;

        Assert.Equal(IconSet.Slate, appearance.IconSet);
    }

    [Fact]
    public void Appearance_differences_break_config_equality()
    {
        var original = ConfigDefaults.Create();

        Assert.NotEqual(
            original,
            original with { Appearance = new AppearanceConfig { IconSet = IconSet.Inverted } });
        Assert.Equal(original, original with { Appearance = new AppearanceConfig() });
    }

    [Fact]
    public void Link_differences_break_config_equality()
    {
        var original = ConfigDefaults.Create();

        Assert.NotEqual(
            original,
            original with { Links = new LinkConfig { External = ExternalLinkPolicy.Never } });
        Assert.Equal(original, original with { Links = new LinkConfig() });
    }

    [Fact]
    public void Logging_differences_break_config_equality()
    {
        // Hot-reload will diff two loads to decide what changed, so a new section that compares
        // equal regardless of its contents would be a silent hole.
        var original = ConfigDefaults.Create();

        Assert.NotEqual(original, original with { Logging = new LoggingConfig { Level = LogVerbosity.Trace } });
        Assert.NotEqual(original, original with { Logging = new LoggingConfig { RetentionDays = 1 } });
        Assert.Equal(original, original with { Logging = new LoggingConfig() });
    }

    [Fact]
    public async Task Golden_file_parses_enums_and_uris()
    {
        var config = await LoadGoldenAsync();
        var teams = config.Workspaces[0].Services[0];
        var azure = config.Workspaces[0].Services[1];

        Assert.Equal(ServiceKind.Teams, teams.Kind);
        Assert.Equal(ServiceKind.Generic, azure.Kind);
        Assert.Equal(new Uri("https://teams.cloud.microsoft/"), teams.Url);
        Assert.Equal(PresenceStrategyKind.Graph, teams.Presence?.Strategy);
        Assert.Equal("james@databyte.example", teams.Presence?.UserPrincipalName);
        Assert.Null(azure.Presence);
        Assert.True(teams.KeepAlive);
        Assert.False(azure.NotificationsEnabled);
    }

    [Fact]
    public async Task Save_then_load_preserves_everything()
    {
        var original = await LoadGoldenAsync();
        using var temp = new TempDirectory();
        var store = new JsonConfigStore(Path.Combine(temp.Path, "muster.json"));

        await store.SaveAsync(original);
        var reloaded = await store.LoadAsync();

        Assert.Equal(original, reloaded);
    }

    [Fact]
    public async Task Written_json_matches_the_golden_file_byte_for_byte()
    {
        var original = await LoadGoldenAsync();
        using var temp = new TempDirectory();
        var store = new JsonConfigStore(Path.Combine(temp.Path, "muster.json"));

        await store.SaveAsync(original);

        var written = Normalise(await File.ReadAllTextAsync(store.Path));
        var golden = Normalise(await File.ReadAllTextAsync(GoldenPath));

        Assert.Equal(golden, written);
    }

    [Fact]
    public async Task Missing_file_is_created_with_defaults()
    {
        using var temp = new TempDirectory();
        var store = new JsonConfigStore(Path.Combine(temp.Path, "muster.json"));

        var config = await store.LoadAsync();

        Assert.True(File.Exists(store.Path));
        Assert.Equal(ConfigDefaults.Create(), config);

        // And the file it just wrote must itself load cleanly.
        Assert.Equal(config, await store.LoadAsync());
    }

    [Fact]
    public async Task Defaults_keep_the_milestone_one_profile_name()
    {
        // Renaming this orphans the profile folder and forces a fresh sign-in.
        var config = ConfigDefaults.Create();
        Assert.Equal("ws-datebyte", config.Workspaces[0].ResolvedProfileName);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Malformed_json_throws_with_the_path()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "muster.json");
        await File.WriteAllTextAsync(path, "{ this is not json");
        var store = new JsonConfigStore(path);

        var ex = await Assert.ThrowsAsync<ConfigException>(() => store.LoadAsync());
        Assert.Equal(path, ex.Path);
    }

    [Fact]
    public async Task Invalid_config_throws_listing_every_problem()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "muster.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "version": 1,
              "workspaces": [
                { "id": "a", "name": "A", "accent": "blue", "services": [
                    { "id": "dupe", "name": "One", "url": "ftp://wrong-scheme.example/" },
                    { "id": "dupe", "name": "Two", "url": "https://ok.example/" }
                ] }
              ]
            }
            """);
        var store = new JsonConfigStore(path);

        var ex = await Assert.ThrowsAsync<ConfigException>(() => store.LoadAsync());

        Assert.Contains(ex.Problems, p => p.Contains("accent"));
        Assert.Contains(ex.Problems, p => p.Contains("duplicate service id"));
        Assert.Contains(ex.Problems, p => p.Contains("absolute http or https"));
    }

    [Fact]
    public async Task Save_is_atomic_and_leaves_no_temp_file()
    {
        using var temp = new TempDirectory();
        var store = new JsonConfigStore(Path.Combine(temp.Path, "muster.json"));

        await store.SaveAsync(ConfigDefaults.Create());

        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    private static async Task<MusterConfig> LoadGoldenAsync()
    {
        await using var stream = File.OpenRead(GoldenPath);
        return await JsonSerializer.DeserializeAsync<MusterConfig>(
            stream, JsonConfigStore.SerializerOptions)
            ?? throw new InvalidOperationException("golden file deserialised to null");
    }

    private static string Normalise(string json) => json.ReplaceLineEndings("\n").TrimEnd();
}
