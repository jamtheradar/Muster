using Muster.Core.Config;

namespace Muster.Core.Presence;

/// <summary>
/// Decides when you are on a call, from microphone acquisition alone. See SPEC section 8.
/// </summary>
/// <remarks>
/// <para>
/// Microphone acquisition is a near-perfect proxy for being in a call and, unlike the Teams DOM,
/// it does not change when Microsoft reshuffles the UI. Nothing here scrapes a page.
/// </para>
/// <para>
/// Acquisitions are counted rather than treated as a toggle: Teams runs across several frames,
/// each with its own copy of bridge.js, and a page can hold more than one stream at a time. A
/// session is active while its count is above zero.
/// </para>
/// </remarks>
public sealed class PresenceCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _outstanding = new(StringComparer.Ordinal);
    private readonly HashSet<string> _callSessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    private TimeSpan _detectDelay;
    private TimeSpan _clearDelay;
    private ITimer? _detectTimer;
    private ITimer? _clearTimer;
    private bool _disposed;

    public PresenceCoordinator(PresenceConfig config, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Apply(config);
    }

    /// <summary>
    /// Takes new delays from a reloaded config. A timer already counting keeps the delay it
    /// started with: restarting it would mean a config save during a call could re-arm the detect
    /// window, or push out the clear that is in the middle of ending the call you just left.
    /// </summary>
    public void Apply(PresenceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_sync)
        {
            _detectDelay = TimeSpan.FromSeconds(config.CallDetectDelaySeconds);
            _clearDelay = TimeSpan.FromSeconds(config.CallClearDelaySeconds);
        }
    }

    public CallState State { get; private set; } = CallState.Idle;

    /// <summary>The session whose microphone started the current call.</summary>
    public string? SourceSessionId { get; private set; }

    /// <summary>Every session that has held audio during the current call.</summary>
    public IReadOnlySet<string> CallSessions
    {
        get
        {
            lock (_sync)
            {
                return new HashSet<string>(_callSessions, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Fires on a timer thread. Marshal before touching UI.</summary>
    public event EventHandler<PresenceStateChangedEventArgs>? StateChanged;

    /// <summary>A session started capturing audio.</summary>
    public void ReportAudioAcquired(string sessionId)
    {
        PresenceStateChangedEventArgs? change = null;

        lock (_sync)
        {
            _outstanding[sessionId] = Count(sessionId) + 1;

            switch (State)
            {
                case CallState.Idle:
                    // Do not declare a call yet. A device test dialog opens the microphone for a
                    // moment, and that must not move anyone's status.
                    StartDetectTimer();
                    break;

                case CallState.InCall:
                    _callSessions.Add(sessionId);
                    break;

                case CallState.Clearing:
                    // Audio came back inside the quiet window: this is the same call, not a new
                    // one, so keep the original source.
                    StopClearTimer();
                    _callSessions.Add(sessionId);
                    change = Transition(CallState.InCall);
                    break;
            }
        }

        Raise(change);
    }

    /// <summary>A session stopped capturing audio.</summary>
    public void ReportAudioReleased(string sessionId)
    {
        PresenceStateChangedEventArgs? change = null;

        lock (_sync)
        {
            var remaining = Count(sessionId) - 1;

            if (remaining <= 0)
            {
                _outstanding.Remove(sessionId);
            }
            else
            {
                _outstanding[sessionId] = remaining;
            }

            if (AnyActive)
            {
                return;
            }

            switch (State)
            {
                case CallState.Idle:
                    // Released before the detect delay elapsed. Never was a call.
                    StopDetectTimer();
                    break;

                case CallState.InCall:
                    change = Transition(CallState.Clearing);
                    StartClearTimer();
                    break;
            }
        }

        Raise(change);
    }

    /// <summary>
    /// A session has gone away. Without this a tab closed mid-call would leave its acquisition
    /// outstanding forever, and the state machine stuck in <see cref="CallState.InCall"/>.
    /// </summary>
    public void ReportSessionGone(string sessionId)
    {
        PresenceStateChangedEventArgs? change = null;

        lock (_sync)
        {
            if (!_outstanding.Remove(sessionId) || AnyActive)
            {
                _callSessions.Remove(sessionId);
                return;
            }

            _callSessions.Remove(sessionId);

            switch (State)
            {
                case CallState.Idle:
                    StopDetectTimer();
                    break;

                case CallState.InCall:
                    change = Transition(CallState.Clearing);
                    StartClearTimer();
                    break;
            }
        }

        Raise(change);
    }

    /// <summary>
    /// The sessions that should be set Busy, given every Teams session in the app. Sessions
    /// taking part in the call are excluded: they are already in it, and forcing Busy on the one
    /// you are actually talking in is exactly the wrong outcome.
    /// </summary>
    public IReadOnlyList<string> BusyTargets(IEnumerable<string> teamsSessionIds)
    {
        lock (_sync)
        {
            if (State == CallState.Idle)
            {
                return [];
            }

            return teamsSessionIds.Where(id => !_callSessions.Contains(id)).ToList();
        }
    }

    private bool AnyActive => _outstanding.Count > 0;

    private int Count(string sessionId)
        => _outstanding.TryGetValue(sessionId, out var count) ? count : 0;

    // Called under the lock. Returns the event to raise once the lock is released.
    private PresenceStateChangedEventArgs? Transition(CallState next)
    {
        if (State == next)
        {
            return null;
        }

        var previous = State;
        State = next;

        if (next == CallState.Idle)
        {
            SourceSessionId = null;
            _callSessions.Clear();
        }

        return new PresenceStateChangedEventArgs(
            previous,
            next,
            SourceSessionId,
            new HashSet<string>(_callSessions, StringComparer.Ordinal));
    }

    private void StartDetectTimer()
    {
        _detectTimer ??= _clock.CreateTimer(
            _ => OnDetectElapsed(), null, _detectDelay, Timeout.InfiniteTimeSpan);
    }

    private void StopDetectTimer()
    {
        _detectTimer?.Dispose();
        _detectTimer = null;
    }

    private void StartClearTimer()
    {
        StopClearTimer();
        _clearTimer = _clock.CreateTimer(
            _ => OnClearElapsed(), null, _clearDelay, Timeout.InfiniteTimeSpan);
    }

    private void StopClearTimer()
    {
        _clearTimer?.Dispose();
        _clearTimer = null;
    }

    private void OnDetectElapsed()
    {
        PresenceStateChangedEventArgs? change;

        lock (_sync)
        {
            StopDetectTimer();

            if (_disposed || State != CallState.Idle || !AnyActive)
            {
                return;
            }

            // Held for long enough. Whoever is holding audio now is in the call; the first of
            // them is the source, the one we must never set Busy.
            _callSessions.Clear();

            foreach (var sessionId in _outstanding.Keys)
            {
                _callSessions.Add(sessionId);
            }

            SourceSessionId = _callSessions.First();
            change = Transition(CallState.InCall);
        }

        Raise(change);
    }

    private void OnClearElapsed()
    {
        PresenceStateChangedEventArgs? change;

        lock (_sync)
        {
            StopClearTimer();

            if (_disposed || State != CallState.Clearing || AnyActive)
            {
                return;
            }

            change = Transition(CallState.Idle);
        }

        Raise(change);
    }

    private void Raise(PresenceStateChangedEventArgs? change)
    {
        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }
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
            StopDetectTimer();
            StopClearTimer();
        }
    }
}
