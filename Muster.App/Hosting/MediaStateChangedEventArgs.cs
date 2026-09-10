namespace Muster.App.Hosting;

/// <summary>
/// A microphone acquisition or release reported by the <c>getUserMedia</c> shim in bridge.js.
/// This is the only call detector: nothing scrapes the Teams DOM.
/// </summary>
public sealed class MediaStateChangedEventArgs(string sessionId, bool acquired, bool video) : EventArgs
{
    public string SessionId { get; } = sessionId;

    /// <summary>True for an acquisition, false for a release.</summary>
    public bool Acquired { get; } = acquired;

    /// <summary>Whether the same stream also carried video. Informational.</summary>
    public bool Video { get; } = video;
}
