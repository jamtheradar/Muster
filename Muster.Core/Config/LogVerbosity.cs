namespace Muster.Core.Config;

/// <summary>
/// How much detail the file log keeps. The names mirror
/// <c>Microsoft.Extensions.Logging.LogLevel</c> one for one, but the enum is declared here
/// because <c>Muster.Core</c> takes no package references at all. The host maps it.
/// </summary>
public enum LogVerbosity
{
    Trace,

    /// <summary>What <c>--verbose</c> selects: unread breakdowns, routing, raw audio events.</summary>
    Debug,

    /// <summary>The default. Session lifecycle, config load, presence transitions.</summary>
    Information,

    Warning,

    Error,

    Critical,

    /// <summary>Nothing is written to the log file at all.</summary>
    None,
}
