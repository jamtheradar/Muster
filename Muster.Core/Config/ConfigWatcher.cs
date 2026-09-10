namespace Muster.Core.Config;

/// <summary>
/// Watches <c>muster.json</c> and turns a change on disk into a <see cref="ConfigDiff"/> the host
/// can apply. See SPEC section 9: the file is hand-editable, so it is watched.
/// </summary>
/// <remarks>
/// <para>
/// The file-watching half is deliberately thin, because it is the half that cannot be unit tested
/// honestly. Everything with a decision in it lives in <see cref="ReloadAsync"/>, which the tests
/// call directly against a real file without going near a <see cref="FileSystemWatcher"/>.
/// </para>
/// <para>
/// Events fire on a thread pool thread. Marshal before touching anything with a window in it.
/// </para>
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    /// <summary>
    /// How long to sit still after a file event. An editor writing a file typically produces two
    /// or three of them, and the app's own atomic save produces both a rename and a change.
    /// </summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A write is not atomic from the reader's point of view, so losing the race is normal and
    /// worth retrying rather than reporting.
    /// </summary>
    public const int ReadAttempts = 4;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly IConfigStore _store;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();

    private FileSystemWatcher? _watcher;
    private ITimer? _debounce;
    private MusterConfig? _current;
    private bool _disposed;

    public ConfigWatcher(IConfigStore store, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised when the file on disk differs from what the app is running on.</summary>
    public event EventHandler<ConfigReloadedEventArgs>? Reloaded;

    /// <summary>
    /// Raised when the file cannot be read or does not validate. The running config is kept: a
    /// half-typed hand edit must never take the shell down with it.
    /// </summary>
    public event EventHandler<ConfigException>? Failed;

    /// <summary>What the app is running on, as far as this watcher knows.</summary>
    public MusterConfig? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>True once the file watcher is actually attached and raising.</summary>
    public bool IsWatching => _watcher is { EnableRaisingEvents: true };

    /// <summary>
    /// Seeds the watcher with the config the app started on and begins watching. Separate from
    /// the constructor because the shell is composed before the config has been loaded.
    /// </summary>
    public void Start(MusterConfig loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        lock (_sync)
        {
            _current = loaded;
        }

        Attach();
    }

    /// <summary>
    /// Reads the file and, if it differs from <see cref="Current"/>, raises
    /// <see cref="Reloaded"/>. Returns what changed, or <see cref="ConfigDiff.None"/> when there
    /// was nothing to do — which includes every failure, because a config that cannot be read is
    /// not a config to act on.
    /// </summary>
    public async Task<ConfigDiff> ReloadAsync(CancellationToken ct = default)
    {
        var previous = Current
            ?? throw new InvalidOperationException($"{nameof(ConfigWatcher)}.{nameof(Start)} was never called.");

        // Some editors delete and recreate rather than writing in place. Reloading through a
        // missing file would have IConfigStore helpfully write the defaults back over it.
        if (!File.Exists(_store.Path))
        {
            return ConfigDiff.None;
        }

        MusterConfig loaded;

        try
        {
            loaded = await ReadWithRetryAsync(ct).ConfigureAwait(false);
        }
        catch (ConfigException ex)
        {
            Failed?.Invoke(this, ex);
            return ConfigDiff.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke(this, new ConfigException(_store.Path, ex.Message, ex));
            return ConfigDiff.None;
        }

        var diff = ConfigDiff.Between(previous, loaded);

        if (diff.IsEmpty)
        {
            return diff;
        }

        lock (_sync)
        {
            _current = loaded;
        }

        Reloaded?.Invoke(this, new ConfigReloadedEventArgs(previous, loaded, diff));
        return diff;
    }

    private async Task<MusterConfig> ReadWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _store.LoadAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                // Still being written. The event that woke us fires before the writer is finished.
                await Task.Delay(RetryDelay, _clock, ct).ConfigureAwait(false);
            }
        }
    }

    private void Attach()
    {
        var directory = Path.GetDirectoryName(_store.Path);
        var file = Path.GetFileName(_store.Path);

        // No directory to watch is not an error worth throwing over: the app runs, it just will
        // not notice hand edits. IConfigStore creates the folder on its first write.
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(file) || !Directory.Exists(directory))
        {
            return;
        }

        var watcher = new FileSystemWatcher(directory, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        // The app's own save writes a .tmp beside the file and moves it into place, so the event
        // that matters is a rename, not a change. Editors do one, the other, or both.
        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += OnFileEvent;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;

        _watcher = watcher;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleReload();

    /// <summary>
    /// The watcher can die on its own: a full buffer, or the folder going away. Losing it silently
    /// would leave the app looking like hot-reload simply stopped working.
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Failed?.Invoke(
            this,
            new ConfigException(_store.Path, "the config file watcher stopped", e.GetException()));

        try
        {
            if (_watcher is { } watcher)
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
        {
            // Gone for good. The settings screen still writes and applies its own changes.
            return;
        }

        // Whatever happened while we were not listening still has to be picked up.
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _debounce?.Dispose();
            _debounce = _clock.CreateTimer(_ => RunReload(), null, DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void RunReload()
    {
        lock (_sync)
        {
            _debounce?.Dispose();
            _debounce = null;

            if (_disposed)
            {
                return;
            }
        }

        // Fire and forget on a timer thread, so nothing here may be allowed to escape.
        _ = ReloadAsync().ContinueWith(
            task => Failed?.Invoke(
                this,
                new ConfigException(_store.Path, task.Exception!.GetBaseException().Message, task.Exception!)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
        }

        if (_watcher is { } watcher)
        {
            watcher.Changed -= OnFileEvent;
            watcher.Created -= OnFileEvent;
            watcher.Renamed -= OnFileEvent;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
            _watcher = null;
        }
    }
}
