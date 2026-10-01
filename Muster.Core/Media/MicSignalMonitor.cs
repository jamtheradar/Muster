namespace Muster.Core.Media;

/// <summary>
/// Turns a stream of microphone level samples into something worth putting on screen: whether the
/// microphone is muted, open but hearing nothing, or actually picking sound up.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does <b>not</b> raise an alarm for a long silence. A microphone that is open
/// and quiet is the normal state of anyone listening in a meeting, so a timer-based "no signal"
/// warning would fire through every call the user sat quietly in — and the failure it is meant to
/// catch, talking into a dead microphone, is indistinguishable from it by elapsed time alone. The
/// live meter is the indicator: speak, and it moves. What this adds on top is the two pieces of
/// context a bare meter cannot show — that a flat meter is Teams' mute rather than a dead device,
/// and how long it has been since anything was heard.
/// </para>
/// <para>
/// There is no timer here, unlike the coordinators: the state advances only when a report arrives,
/// and reports arrive on the thread the host reads web messages on, which is the UI thread. So
/// nothing here needs marshalling and nothing fires from the thread pool.
/// </para>
/// </remarks>
public sealed class MicSignalMonitor
{
    /// <summary>
    /// RMS below this counts as silence. Set low on purpose: Teams applies noise suppression
    /// before the track this samples, so a genuinely silent room arrives at nearly zero while a
    /// quiet talker still clears it comfortably.
    /// </summary>
    public const double DefaultThreshold = 0.01;

    /// <summary>
    /// How long sound keeps the meter reading <see cref="MicSignal.Live"/> after it stops. Long
    /// enough to survive the gaps between words, short enough that it follows the voice.
    /// </summary>
    public static readonly TimeSpan DefaultHearingWindow = TimeSpan.FromSeconds(2.5);

    private readonly TimeProvider _clock;
    private readonly double _threshold;
    private readonly TimeSpan _hearingWindow;
    private readonly Dictionary<string, Capture> _captures = new(StringComparer.Ordinal);

    public MicSignalMonitor(
        TimeProvider? clock = null,
        double threshold = DefaultThreshold,
        TimeSpan? hearingWindow = null)
    {
        _clock = clock ?? TimeProvider.System;
        _threshold = threshold;
        _hearingWindow = hearingWindow ?? DefaultHearingWindow;
    }

    /// <summary>The aggregate state across every session currently capturing.</summary>
    public MicSignal Signal { get; private set; } = MicSignal.Unknown;

    /// <summary>Peak level over the last reporting interval, 0 to 1. Zero while muted.</summary>
    public double Level { get; private set; }

    /// <summary>When sound was last picked up, or null if none has been.</summary>
    public DateTimeOffset? LastHeard { get; private set; }

    /// <summary>
    /// Raised after every report, not only when <see cref="Signal"/> changes: the level drives a
    /// meter, which has to follow the voice rather than the state. That makes this a few events a
    /// second per capturing session, so handlers must stay cheap.
    /// </summary>
    public event EventHandler<MicSignalChangedEventArgs>? Changed;

    /// <summary>
    /// Records one level sample from a session.
    /// </summary>
    /// <param name="sessionId">The session whose page reported it.</param>
    /// <param name="level">Peak RMS over the reporting interval, 0 to 1.</param>
    /// <param name="enabled">
    /// The track's <c>enabled</c> flag. False is Teams' own mute button, which produces digital
    /// silence — without this, muting would look exactly like a broken microphone.
    /// </param>
    public void Report(string sessionId, double level, bool enabled)
    {
        if (!_captures.TryGetValue(sessionId, out var capture))
        {
            capture = new Capture();
            _captures[sessionId] = capture;
        }

        capture.Enabled = enabled;
        capture.Level = enabled ? Math.Clamp(level, 0, 1) : 0;

        if (enabled && level >= _threshold)
        {
            capture.LastHeard = _clock.GetUtcNow();
        }

        Evaluate();
    }

    /// <summary>
    /// Drops a session, because its microphone was released or the session went away. A meter
    /// still reading against a session that has gone is worse than no meter at all.
    /// </summary>
    public void Forget(string sessionId)
    {
        if (_captures.Remove(sessionId))
        {
            Evaluate();
        }
    }

    /// <summary>Drops every session, for when the call state returns to idle.</summary>
    public void Reset()
    {
        if (_captures.Count == 0)
        {
            return;
        }

        _captures.Clear();
        Evaluate();
    }

    private void Evaluate()
    {
        var now = _clock.GetUtcNow();
        var signal = MicSignal.Unknown;
        var level = 0d;
        DateTimeOffset? lastHeard = null;

        foreach (var capture in _captures.Values)
        {
            if (!capture.Enabled)
            {
                if (signal < MicSignal.Muted)
                {
                    signal = MicSignal.Muted;
                }

                continue;
            }

            level = Math.Max(level, capture.Level);

            if (capture.LastHeard is { } heard)
            {
                if (lastHeard is not { } best || heard > best)
                {
                    lastHeard = heard;
                }

                if (now - heard <= _hearingWindow)
                {
                    signal = MicSignal.Live;
                    continue;
                }
            }

            if (signal < MicSignal.Quiet)
            {
                signal = MicSignal.Quiet;
            }
        }

        Signal = signal;
        Level = level;
        LastHeard = lastHeard;

        Changed?.Invoke(this, new MicSignalChangedEventArgs(signal, level, lastHeard));
    }

    private sealed class Capture
    {
        public bool Enabled { get; set; }

        public double Level { get; set; }

        public DateTimeOffset? LastHeard { get; set; }
    }
}
