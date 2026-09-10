using Muster.Core.Config;

namespace Muster.Core.Hosting;

/// <summary>
/// Decides which live sessions should be asleep. See SPEC section 6.1.
/// </summary>
/// <remarks>
/// <para>
/// The rules are short and the consequences of getting them wrong are not, which is why they live
/// here rather than in the shell: a service that suspends when it should not have stops delivering
/// the notifications the app exists to surface, and does it silently.
/// </para>
/// <para>
/// The unit of idleness is the workspace, not the tab. SPEC 6.1 says nothing in the active
/// workspace is ever suspended, so a background tab left open beside the one being read keeps
/// running; the clock starts when the whole workspace goes off screen.
/// </para>
/// <para>
/// This decides and announces; it never remembers what actually happened. Suspension is best
/// effort — WebView2 declines for a page holding audio, and resumes on its own for a navigation —
/// so a "suspended" flag kept here would drift out of step with the browser within minutes.
/// Instead every tick re-announces every eligible session and the host, which can see the real
/// state, no-ops when there is nothing to do. That costs one property read per background session
/// per tick and cannot get stuck.
/// </para>
/// </remarks>
public sealed class SuspensionCoordinator : IDisposable
{
    /// <summary>
    /// How often eligibility is re-checked. A session therefore sleeps up to one interval after
    /// its deadline, which for a five-minute idle window is not worth a tighter timer.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    private HashSet<string> _callSessions = new(StringComparer.Ordinal);
    private SuspensionConfig _config = new();
    private string? _activeWorkspaceId;
    private ITimer? _timer;
    private bool _disposed;

    public SuspensionCoordinator(SuspensionConfig config, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Apply(config);
    }

    /// <summary>Asks the host to put a session to sleep. Best effort; it may decline.</summary>
    public event EventHandler<SessionDescriptor>? SuspendRequested;

    /// <summary>Asks the host to wake a session, whether or not it is actually asleep.</summary>
    public event EventHandler<SessionDescriptor>? ResumeRequested;

    /// <summary>How many sessions are currently eligible to be asleep, for the status bar.</summary>
    public int SuspendableCount
    {
        get
        {
            lock (_sync)
            {
                var now = _clock.GetUtcNow();
                return _sessions.Values.Count(entry => IsDue(entry, now));
            }
        }
    }

    /// <summary>
    /// Takes settings from a reloaded config. Turning suspension off wakes everything at once
    /// rather than waiting for each workspace to be visited: the setting exists to be an escape
    /// hatch, and an escape hatch that takes effect gradually is not one.
    /// </summary>
    public void Apply(SuspensionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<SessionDescriptor> wake = [];

        lock (_sync)
        {
            var wasEnabled = _config.Enabled;
            _config = config;

            if (config.Enabled)
            {
                StartTimer();
            }
            else
            {
                StopTimer();

                if (wasEnabled)
                {
                    wake = _sessions.Values.Select(entry => entry.Descriptor).ToList();
                }
            }
        }

        Raise(ResumeRequested, wake);
    }

    /// <summary>
    /// Starts watching a session, or refreshes the descriptor of one already being watched. Its
    /// idle clock runs from now if it starts off screen.
    /// </summary>
    /// <remarks>
    /// Re-tracking is how a config reload lands: <c>keepAlive</c> can be edited without the
    /// session being recreated, and it is the flag that decides whether the session may sleep at
    /// all, so the descriptor held here has to be replaced or the change never takes effect. The
    /// clock survives, because the session has not gone anywhere.
    /// </remarks>
    public void Track(SessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_sync)
        {
            if (_sessions.TryGetValue(descriptor.Id, out var known))
            {
                _sessions[descriptor.Id] = known with { Descriptor = descriptor };
                return;
            }

            var active = IsActiveWorkspace(descriptor.WorkspaceId);
            _sessions[descriptor.Id] = new Entry(descriptor, active ? null : _clock.GetUtcNow());
        }
    }

    /// <summary>Stops watching a torn-down session.</summary>
    public void Forget(string sessionId)
    {
        lock (_sync)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <summary>
    /// Called when the visible workspace changes. Everything in the workspace being shown wakes
    /// immediately — SPEC 6.1 resumes on the switch, not on the next tick, or the user watches a
    /// dead page while a timer catches up — and everything left behind starts its clock.
    /// </summary>
    public void SetActiveWorkspace(string? workspaceId)
    {
        List<SessionDescriptor> wake;

        lock (_sync)
        {
            if (string.Equals(_activeWorkspaceId, workspaceId, StringComparison.Ordinal))
            {
                return;
            }

            _activeWorkspaceId = workspaceId;
            var now = _clock.GetUtcNow();
            wake = [];

            foreach (var (id, entry) in _sessions.ToList())
            {
                if (IsActiveWorkspace(entry.Descriptor.WorkspaceId))
                {
                    _sessions[id] = entry with { IdleSince = null };
                    wake.Add(entry.Descriptor);
                }
                else
                {
                    // Already counting down: keep the original timestamp, so flicking through the
                    // rail does not reset every other workspace's clock on the way past.
                    _sessions[id] = entry with { IdleSince = entry.IdleSince ?? now };
                }
            }
        }

        Raise(ResumeRequested, wake);
    }

    /// <summary>
    /// The sessions currently holding audio, from the presence coordinator. They are never
    /// suspended whatever their idle time says.
    /// </summary>
    /// <remarks>
    /// WebView2 refuses to suspend a page capturing audio anyway, so this is belt and braces. It
    /// is stated here rather than left to the browser because "the call kept working" is not
    /// something to find out by experiment on a live call.
    /// </remarks>
    public void SetCallSessions(IReadOnlySet<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        lock (_sync)
        {
            _callSessions = new HashSet<string>(sessionIds, StringComparer.Ordinal);
        }
    }

    /// <summary>Whether a session is eligible to be asleep right now.</summary>
    public bool IsSuspendable(string sessionId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var entry) && IsDue(entry, _clock.GetUtcNow());
        }
    }

    /// <summary>Re-announces every session that should be asleep. Called on the timer.</summary>
    public void Evaluate()
    {
        List<SessionDescriptor> sleep;

        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            sleep = _sessions.Values
                .Where(entry => IsDue(entry, now))
                .Select(entry => entry.Descriptor)
                .ToList();
        }

        Raise(SuspendRequested, sleep);
    }

    private bool IsDue(Entry entry, DateTimeOffset now)
        => _config.Enabled
        && !entry.Descriptor.KeepAlive
        && !_callSessions.Contains(entry.Descriptor.Id)
        && entry.IdleSince is { } since
        && now - since >= _config.IdleTimeout;

    private bool IsActiveWorkspace(string workspaceId)
        => _activeWorkspaceId is { } active && string.Equals(active, workspaceId, StringComparison.Ordinal);

    private void Raise(EventHandler<SessionDescriptor>? handler, List<SessionDescriptor> descriptors)
    {
        if (handler is null)
        {
            return;
        }

        // Outside the lock: the host answers these by touching WebView2 on the UI thread.
        foreach (var descriptor in descriptors)
        {
            handler(this, descriptor);
        }
    }

    private void StartTimer()
        => _timer ??= _clock.CreateTimer(_ => Evaluate(), null, PollInterval, PollInterval);

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
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
            StopTimer();
            _sessions.Clear();
        }
    }

    /// <param name="IdleSince">Null while the session's workspace is the visible one.</param>
    private sealed record Entry(SessionDescriptor Descriptor, DateTimeOffset? IdleSince);
}
