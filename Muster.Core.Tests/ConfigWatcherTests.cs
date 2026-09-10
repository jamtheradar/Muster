using Muster.Core.Config;

namespace Muster.Core.Tests;

/// <summary>
/// The reload decision, tested against a real file. The <see cref="FileSystemWatcher"/> itself is
/// exercised once, at the end, only to prove it is wired up at all; everything with a rule in it
/// goes through <see cref="ConfigWatcher.ReloadAsync"/> directly, where the timing is ours.
/// </summary>
public sealed class ConfigWatcherTests
{
    [Fact]
    public async Task Reloading_before_start_is_a_programming_error()
    {
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        using var watcher = new ConfigWatcher(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => watcher.ReloadAsync());
    }

    [Fact]
    public async Task An_unchanged_file_raises_nothing()
    {
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        var config = ConfigDefaults.Create();
        await store.SaveAsync(config);

        using var watcher = new ConfigWatcher(store);
        var raised = 0;
        watcher.Reloaded += (_, _) => raised++;
        watcher.Start(config);

        var diff = await watcher.ReloadAsync();

        Assert.True(diff.IsEmpty);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_changed_file_raises_with_the_diff_and_moves_current_on()
    {
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        var original = ConfigDefaults.Create();
        await store.SaveAsync(original);

        var edited = original with { Notifications = new NotificationConfig { ToastsEnabled = false } };
        await store.SaveAsync(edited);

        using var watcher = new ConfigWatcher(store);
        ConfigReloadedEventArgs? seen = null;
        watcher.Reloaded += (_, e) => seen = e;
        watcher.Start(original);

        var diff = await watcher.ReloadAsync();

        Assert.NotNull(seen);
        Assert.True(diff.NotificationsChanged);
        Assert.Equal(original, seen.Previous);
        Assert.Equal(edited, seen.Current);
        Assert.Equal(edited, watcher.Current);

        // Applied once, not once per subsequent event.
        Assert.True((await watcher.ReloadAsync()).IsEmpty);
    }

    [Fact]
    public async Task A_broken_file_is_reported_and_the_running_config_kept()
    {
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        var running = ConfigDefaults.Create();
        await store.SaveAsync(running);

        using var watcher = new ConfigWatcher(store);
        ConfigException? failure = null;
        watcher.Failed += (_, e) => failure = e;
        watcher.Reloaded += (_, _) => Assert.Fail("A config that will not parse must not be applied.");
        watcher.Start(running);

        await File.WriteAllTextAsync(store.Path, "{ \"workspaces\": [ ");

        var diff = await watcher.ReloadAsync();

        Assert.NotNull(failure);
        Assert.True(diff.IsEmpty);
        Assert.Equal(running, watcher.Current);
    }

    [Fact]
    public async Task An_invalid_file_is_reported_rather_than_applied()
    {
        // Parses fine, fails the validator: a duplicate service id, which is the kind of thing a
        // copy-paste edit produces. Applying it would stand up two sessions on one id.
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        var running = ConfigDefaults.Create();
        await store.SaveAsync(running);

        using var watcher = new ConfigWatcher(store);
        ConfigException? failure = null;
        watcher.Failed += (_, e) => failure = e;
        watcher.Start(running);

        await File.WriteAllTextAsync(store.Path, InvalidJson);

        Assert.True((await watcher.ReloadAsync()).IsEmpty);
        Assert.NotNull(failure);
        Assert.Contains("duplicate service id", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(running, watcher.Current);
    }

    [Fact]
    public async Task The_settings_screens_own_save_comes_back_as_a_reload()
    {
        // The settings screen is a pure editor: it writes the file and lets the watcher apply it,
        // so a hand edit and a save go down exactly the same path.
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        var original = ConfigDefaults.Create();
        await store.SaveAsync(original);

        using var watcher = new ConfigWatcher(store);
        ConfigReloadedEventArgs? seen = null;
        watcher.Reloaded += (_, e) => seen = e;
        watcher.Start(original);

        await store.SaveAsync(original with { Logging = new LoggingConfig { Level = LogVerbosity.Trace } });

        await watcher.ReloadAsync();

        Assert.NotNull(seen);
        Assert.True(seen.Diff.LoggingChanged);
        Assert.False(seen.Diff.RequiresRestart);
    }

    [Fact]
    public async Task A_missing_file_is_never_replaced_with_defaults()
    {
        // IConfigStore writes the defaults when the file is absent, which is right at startup and
        // catastrophic here: an editor that deletes and recreates would lose the user's config.
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        var running = ConfigDefaults.Create() with { Presence = new PresenceConfig { Enabled = true } };
        await store.SaveAsync(running);

        using var watcher = new ConfigWatcher(store);
        watcher.Reloaded += (_, _) => Assert.Fail("There was no file to reload from.");
        watcher.Start(running);

        File.Delete(store.Path);

        Assert.True((await watcher.ReloadAsync()).IsEmpty);
        Assert.False(File.Exists(store.Path));
        Assert.Equal(running, watcher.Current);
    }

    [Fact]
    public async Task A_hand_edit_on_disk_reaches_the_reloaded_event()
    {
        // The one test that goes through the real FileSystemWatcher. It is here because everything
        // above would still pass if Start never attached anything at all.
        using var temp = new TempDirectory();
        var store = StoreIn(temp);
        var original = ConfigDefaults.Create();
        await store.SaveAsync(original);

        using var watcher = new ConfigWatcher(store);
        var reloaded = new TaskCompletionSource<ConfigReloadedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Reloaded += (_, e) => reloaded.TrySetResult(e);
        watcher.Start(original);

        Assert.True(watcher.IsWatching);

        var edited = original with { Presence = original.Presence with { CallClearDelaySeconds = 42 } };
        await store.SaveAsync(edited);

        var completed = await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(reloaded.Task, completed);

        var seen = await reloaded.Task;
        Assert.True(seen.Diff.PresenceChanged);
        Assert.Equal(42, seen.Current.Presence.CallClearDelaySeconds);
    }

    private static JsonConfigStore StoreIn(TempDirectory temp)
        => new(Path.Combine(temp.Path, "muster.json"));

    private const string InvalidJson = """
        {
          "version": 1,
          "workspaces": [
            {
              "id": "db",
              "name": "DataByte",
              "accent": "#2D7D9A",
              "services": [
                { "id": "teams", "name": "Teams", "url": "https://teams.cloud.microsoft/" },
                { "id": "teams", "name": "Teams again", "url": "https://teams.cloud.microsoft/" }
              ]
            }
          ]
        }
        """;
}
