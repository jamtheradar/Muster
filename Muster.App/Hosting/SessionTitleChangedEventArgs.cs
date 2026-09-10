namespace Muster.App.Hosting;

/// <summary>A document title change, tagged with the session it came from.</summary>
public sealed class SessionTitleChangedEventArgs(string sessionId, string title) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public string Title { get; } = title;
}
