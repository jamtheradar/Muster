namespace Muster.App.Hosting;

/// <summary>
/// One microphone level sample from the metering shim in bridge.js, roughly four a second while
/// a session holds a track.
/// </summary>
/// <remarks>
/// Untrusted page input like everything else arriving over the bridge, so the level is clamped
/// downstream rather than believed. See <see cref="Muster.Core.Media.MicSignalMonitor"/>.
/// </remarks>
public sealed class AudioLevelEventArgs(string sessionId, double level, bool enabled) : EventArgs
{
    public string SessionId { get; } = sessionId;

    /// <summary>Peak RMS over the reporting interval, nominally 0 to 1.</summary>
    public double Level { get; } = level;

    /// <summary>
    /// The track's <c>enabled</c> flag. False is the page's own mute — Teams' mute button leaves
    /// the track open and feeds silence, which without this looks exactly like a dead microphone.
    /// </summary>
    public bool Enabled { get; } = enabled;
}
