namespace Muster.Core.Diagnostics;

/// <summary>
/// Where the log files live and what is in there. Kept in <c>Muster.Core</c> so the retention
/// sweep is unit-testable: it deletes files, and deleting files is worth a test.
/// </summary>
public static class LogFolder
{
    /// <summary>
    /// The only files the sweep will ever touch. Anything else in the folder, whether the user
    /// put it there or another tool did, is left strictly alone.
    /// </summary>
    public const string FilePattern = "muster-*.log";

    /// <summary><c>%LOCALAPPDATA%\Muster\logs</c>.</summary>
    public static string Default { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Muster",
        "logs");

    /// <summary>The folder a configured value resolves to. Blank or null means the default.</summary>
    public static string Resolve(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? Default : Path.GetFullPath(configured.Trim());

    /// <summary>
    /// Every log file in the folder, newest first. A missing or unreadable folder is empty rather
    /// than an error: the settings screen must open even when logging has never worked.
    /// </summary>
    public static IReadOnlyList<LogFile> List(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return new DirectoryInfo(directory)
                .EnumerateFiles(FilePattern, SearchOption.TopDirectoryOnly)
                .Select(file => new LogFile(
                    file.FullName,
                    file.Name,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTime)))
                .OrderByDescending(file => file.LastWritten)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Total bytes the log folder is using.</summary>
    public static long TotalBytes(IEnumerable<LogFile> files) => files.Sum(file => file.Bytes);

    /// <summary>
    /// Deletes log files last written more than <paramref name="retentionDays"/> days before
    /// <paramref name="now"/>, and returns how many went. A retention of zero or less keeps
    /// everything.
    /// </summary>
    /// <remarks>
    /// Never throws. This runs during startup, and a log folder that cannot be tidied is not a
    /// reason to fail to launch.
    /// </remarks>
    public static int Prune(string directory, int retentionDays, DateTimeOffset now)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = now.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var file in List(directory))
        {
            if (file.LastWritten >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file.Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use, or someone else owns it. Leave it and try again next launch.
            }
        }

        return deleted;
    }
}
