using Muster.Core.Config;
using Muster.Core.Diagnostics;

namespace Muster.Core.Tests;

/// <summary>
/// The retention sweep deletes files on startup, so what it will and will not touch is worth
/// pinning down.
/// </summary>
public sealed class LogFolderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resolve_falls_back_to_the_default_folder()
    {
        Assert.Equal(LogFolder.Default, LogFolder.Resolve(null));
        Assert.Equal(LogFolder.Default, LogFolder.Resolve("   "));
        Assert.Equal(Path.GetFullPath(@"D:\logs"), LogFolder.Resolve(@"D:\logs"));
    }

    [Fact]
    public void Listing_a_missing_folder_is_empty_rather_than_an_error()
    {
        // The settings screen has to open even when logging has never worked.
        Assert.Empty(LogFolder.List(Path.Combine(Path.GetTempPath(), "muster-tests", "nope")));
    }

    [Fact]
    public void Files_are_listed_newest_first_with_their_size()
    {
        using var temp = new TempDirectory();
        Write(temp, "muster-2026-08-27.log", Now.AddDays(-2), "old");
        Write(temp, "muster-2026-08-29.log", Now, "today");

        var files = LogFolder.List(temp.Path);

        Assert.Equal(["muster-2026-08-29.log", "muster-2026-08-27.log"], files.Select(f => f.Name));
        Assert.Equal(5, files[0].Bytes);
        Assert.Equal("5 B", files[0].SizeText);
    }

    [Fact]
    public void Prune_deletes_only_files_older_than_the_retention_window()
    {
        using var temp = new TempDirectory();
        Write(temp, "muster-2026-08-01.log", Now.AddDays(-28), "stale");
        Write(temp, "muster-2026-08-20.log", Now.AddDays(-9), "edge");
        Write(temp, "muster-2026-08-29.log", Now, "today");

        var deleted = LogFolder.Prune(temp.Path, retentionDays: 14, Now);

        Assert.Equal(1, deleted);
        Assert.Equal(
            ["muster-2026-08-29.log", "muster-2026-08-20.log"],
            LogFolder.List(temp.Path).Select(f => f.Name));
    }

    [Fact]
    public void Prune_never_touches_anything_that_is_not_a_muster_log()
    {
        // The folder is a real folder on the user's disk. It only ever deletes what it wrote.
        using var temp = new TempDirectory();
        Write(temp, "muster-2026-01-01.log", Now.AddDays(-240), "ours");
        Write(temp, "notes.txt", Now.AddDays(-240), "theirs");
        Write(temp, "crash-2026-01-01.log", Now.AddDays(-240), "someone else's");

        LogFolder.Prune(temp.Path, retentionDays: 7, Now);

        Assert.False(File.Exists(Path.Combine(temp.Path, "muster-2026-01-01.log")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "crash-2026-01-01.log")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_retention_of_zero_or_less_keeps_everything(int retentionDays)
    {
        using var temp = new TempDirectory();
        Write(temp, "muster-2020-01-01.log", Now.AddDays(-2000), "ancient");

        Assert.Equal(0, LogFolder.Prune(temp.Path, retentionDays, Now));
        Assert.Single(LogFolder.List(temp.Path));
    }

    [Fact]
    public void Prune_on_a_missing_folder_does_not_throw()
    {
        // It runs during startup. A log folder that cannot be tidied is not a reason not to launch.
        var missing = Path.Combine(Path.GetTempPath(), "muster-tests", Guid.NewGuid().ToString("N"));

        Assert.Equal(0, LogFolder.Prune(missing, LoggingConfig.DefaultRetentionDays, Now));
    }

    [Fact]
    public void Total_bytes_adds_the_folder_up()
    {
        using var temp = new TempDirectory();
        Write(temp, "muster-2026-08-28.log", Now.AddDays(-1), "12345");
        Write(temp, "muster-2026-08-29.log", Now, "678");

        Assert.Equal(8, LogFolder.TotalBytes(LogFolder.List(temp.Path)));
    }

    private static void Write(TempDirectory temp, string name, DateTimeOffset written, string content)
    {
        var path = Path.Combine(temp.Path, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, written.LocalDateTime);
    }
}
