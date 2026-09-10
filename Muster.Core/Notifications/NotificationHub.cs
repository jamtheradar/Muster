namespace Muster.Core.Notifications;

/// <summary>
/// Merges the two notification ingestion paths, drops duplicates, and keeps the history the
/// panel renders. Knows nothing about WebView2 or WPF, so the merge rules are testable.
/// </summary>
public sealed class NotificationHub(int historyLimit, TimeProvider? clock = null)
{
    /// <summary>
    /// How long after a notification an identical one counts as the same event. The two paths
    /// fire within milliseconds of each other; five seconds is loose enough to catch a slow
    /// service worker without merging two genuine messages that happen to match.
    /// </summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(5);

    private readonly List<NotificationRecord> _history = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _historyLimit = Math.Max(1, historyLimit);

    /// <summary>Newest first, capped at the configured limit.</summary>
    public IReadOnlyList<NotificationRecord> History => _history;

    /// <summary>
    /// How many entries to keep. Settable so a saved config change applies without a relaunch;
    /// lowering it trims what is already there, which is what the panel is showing.
    /// </summary>
    public int HistoryLimit
    {
        get => _historyLimit;
        set
        {
            _historyLimit = Math.Max(1, value);

            if (Trim())
            {
                Trimmed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised for each notification that survives deduplication.</summary>
    public event EventHandler<NotificationRecord>? Received;

    /// <summary>Raised when the history is emptied.</summary>
    public event EventHandler? Cleared;

    /// <summary>
    /// Raised when a lowered <see cref="HistoryLimit"/> dropped entries. Separate from
    /// <see cref="Cleared"/> because the history is now shorter, not empty, but the panel has to
    /// be rebuilt from it either way.
    /// </summary>
    public event EventHandler? Trimmed;

    /// <summary>
    /// Adds a notification unless it duplicates a recent one. Always returns the canonical
    /// record: for a duplicate that is the one already stored, so the caller can still attach
    /// state such as a live WebView2 notification handle to it.
    /// </summary>
    /// <remarks>
    /// Callers must already have applied the per-service <c>muted</c> and
    /// <c>notificationsEnabled</c> flags: filtering happens at ingestion so a muted service never
    /// pollutes the history, rather than being hidden at display time.
    /// </remarks>
    public NotificationIngestResult Ingest(
        string sessionId,
        string workspaceId,
        string serviceName,
        string title,
        string body,
        NotificationSource source)
    {
        var now = _clock.GetUtcNow();
        var record = new NotificationRecord(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            WorkspaceId: workspaceId,
            ServiceName: serviceName,
            Title: title ?? string.Empty,
            Body: body ?? string.Empty,
            ReceivedAt: now,
            Source: source);

        if (FindDuplicate(record, now) is { } existing)
        {
            return new NotificationIngestResult(existing, IsNew: false);
        }

        _history.Insert(0, record);
        Trim();

        Received?.Invoke(this, record);
        return new NotificationIngestResult(record, IsNew: true);
    }

    /// <summary>Finds a record by id, for toast activation.</summary>
    public NotificationRecord? Find(string id)
        => _history.FirstOrDefault(record => record.Id == id);

    public void Clear()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _history.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops everything past the limit. True if anything went.</summary>
    private bool Trim()
    {
        if (_history.Count <= _historyLimit)
        {
            return false;
        }

        _history.RemoveRange(_historyLimit, _history.Count - _historyLimit);
        return true;
    }

    // History is newest first, so stop as soon as we are past the window.
    private NotificationRecord? FindDuplicate(NotificationRecord candidate, DateTimeOffset now)
    {
        foreach (var existing in _history)
        {
            if (now - existing.ReceivedAt > DedupeWindow)
            {
                return null;
            }

            if (existing.DedupeKey == candidate.DedupeKey)
            {
                return existing;
            }
        }

        return null;
    }
}
