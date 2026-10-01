namespace Muster.Core.Media;

/// <summary>
/// A snapshot of what the microphone is doing, raised by <see cref="MicSignalMonitor"/>.
/// </summary>
/// <param name="Signal">The aggregate state across every capturing session.</param>
/// <param name="Level">
/// Peak level over the last reporting interval, 0 to 1, across every unmuted session. Zero while
/// muted: a muted track is digital silence, and drawing that as a flat meter would read as a dead
/// microphone rather than a switched-off one.
/// </param>
/// <param name="LastHeard">
/// When sound was last picked up, or null if none has been since capture started. What makes the
/// difference between "you are not talking" and "nothing is getting through".
/// </param>
public sealed record MicSignalChangedEventArgs(
    MicSignal Signal,
    double Level,
    DateTimeOffset? LastHeard);
