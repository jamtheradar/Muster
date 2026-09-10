namespace Muster.Core.Config;

/// <summary>
/// File logging settings. The log is a diagnostic aid, not telemetry: nothing leaves the machine,
/// so the only decisions here are how much to write, how long to keep it and where to put it.
/// </summary>
public sealed record LoggingConfig
{
    /// <summary>
    /// Minimum level written to the log file. <c>--verbose</c> still overrides this downwards, so
    /// a support session never has to edit the config first.
    /// </summary>
    public LogVerbosity Level { get; init; } = LogVerbosity.Information;

    /// <summary>
    /// Log files older than this are deleted at startup. <c>0</c> keeps them forever, which is
    /// the only way to end up with an unbounded folder.
    /// </summary>
    public int RetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>
    /// Overrides <c>%LOCALAPPDATA%\Muster\logs</c>. Null means the default. Must be absolute: a
    /// relative path would resolve against the working directory, which for a shortcut launch is
    /// not anywhere the user would think to look.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>Two weeks is long enough to still have the log for "it did it again last week".</summary>
    public const int DefaultRetentionDays = 14;

    /// <summary>Ten years. Past this the number is a typo, not an intention.</summary>
    public const int MaxRetentionDays = 3650;
}
