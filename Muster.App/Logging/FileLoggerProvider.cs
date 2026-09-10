using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Muster.Core.Config;
using Muster.Core.Diagnostics;

namespace Muster.App.Logging;

/// <summary>
/// A deliberately small append-only file sink, one file per day under
/// <c>%LOCALAPPDATA%\Muster\logs</c> unless the config points somewhere else. Enough to diagnose a
/// sign-in or WebView2 failure after the fact without attaching a debugger. Not telemetry:
/// nothing leaves the machine.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private volatile LogVerbosity _minimum;
    private DateOnly _day;
    private string _path;

    public FileLoggerProvider(LoggingConfig? settings = null)
    {
        settings ??= new LoggingConfig();
        _minimum = settings.Level;

        // A configured folder that cannot be created must not stop the app starting, so fall
        // back to the default rather than throwing out of the composition root.
        Directory = EnsureDirectory(LogFolder.Resolve(settings.Directory))
            ?? EnsureDirectory(LogFolder.Default)
            ?? Path.GetTempPath();

        _day = DateOnly.FromDateTime(DateTime.Now);
        _path = FileFor(_day);
    }

    /// <summary>Folder the log files are written to. Surfaced in the UI so it is findable.</summary>
    public string Directory { get; }

    /// <summary>The file being appended to right now. Rolls at midnight.</summary>
    public string CurrentFile
    {
        get
        {
            lock (_sync)
            {
                return _path;
            }
        }
    }

    /// <summary>
    /// The most detail the config is allowed to take away, set once from <c>--verbose</c>.
    /// </summary>
    /// <remarks>
    /// It lives here rather than in the caller because the config is applied at startup and again
    /// on every reload, and each of those would otherwise silently undo the flag. That is exactly
    /// what it used to do: <c>--verbose</c> held for the half second between the logger being
    /// built and the first <c>LiveSettings.Apply</c>, which is worse than not having the flag,
    /// because the startup line said Debug while the file was filtered to Information.
    /// </remarks>
    public LogVerbosity Floor { get; init; } = LogVerbosity.None;

    /// <summary>
    /// Minimum level written. Settable so the settings screen can turn the detail up without a
    /// restart, which is the one logging change worth having live: by the time you want debug
    /// output, restarting is what loses you the thing you were trying to catch. Never allowed
    /// above <see cref="Floor"/>, so a config reload cannot cancel <c>--verbose</c>.
    /// </summary>
    public LogVerbosity MinimumLevel
    {
        get => _minimum;
        set => _minimum = value > Floor ? Floor : value;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>Maps to the level the logging builder wants, for the providers we do not own.</summary>
    public static LogLevel ToLogLevel(LogVerbosity verbosity) => verbosity switch
    {
        LogVerbosity.Trace => LogLevel.Trace,
        LogVerbosity.Debug => LogLevel.Debug,
        LogVerbosity.Information => LogLevel.Information,
        LogVerbosity.Warning => LogLevel.Warning,
        LogVerbosity.Error => LogLevel.Error,
        LogVerbosity.Critical => LogLevel.Critical,
        _ => LogLevel.None,
    };

    private string FileFor(DateOnly day) => Path.Combine(Directory, $"muster-{day:yyyy-MM-dd}.log");

    private static string? EnsureDirectory(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(category).Append(": ")
            .Append(message)
            .AppendLine();

        if (exception is not null)
        {
            line.AppendLine(exception.ToString());
        }

        lock (_sync)
        {
            // Roll on date, or a session left running overnight keeps appending to yesterday and
            // the retention sweep never gets to close the file off.
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today != _day)
            {
                _day = today;
                _path = FileFor(today);
            }

            try
            {
                // BOM on creation, so tools that guess ANSI still read the file as UTF-8.
                File.AppendAllText(_path, line.ToString(), Utf8WithBom);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trc",
        LogLevel.Debug => "dbg",
        LogLevel.Information => "inf",
        LogLevel.Warning => "wrn",
        LogLevel.Error => "err",
        LogLevel.Critical => "crt",
        _ => "non",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            var minimum = provider._minimum;
            return minimum != LogVerbosity.None && logLevel >= ToLogLevel(minimum);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
