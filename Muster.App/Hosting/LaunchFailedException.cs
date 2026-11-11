namespace Muster.App.Hosting;

/// <summary>
/// A launched service that never came up. Carries the last lines the process wrote, for the tab
/// rather than the log: they are the useful part of the diagnosis, and they may hold a token.
/// </summary>
public sealed class LaunchFailedException(string message, IReadOnlyList<string> tail) : Exception(message)
{
    public IReadOnlyList<string> Tail { get; } = tail;
}
