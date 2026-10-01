namespace Muster.App.Hosting;

/// <summary>
/// Whether a session is currently playing audio, from WebView2's own
/// <c>CoreWebView2.IsDocumentPlayingAudio</c>.
/// </summary>
/// <remarks>
/// Nothing is injected for this and nothing is parsed: the browser already knows, because it is
/// the one rendering the audio. It answers the other half of "is my headset working" — if Teams
/// is playing and you can hear nothing, the fault is on the output path rather than in the call.
/// </remarks>
public sealed class AudioPlaybackEventArgs(string sessionId, bool playing) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public bool Playing { get; } = playing;
}
