namespace Muster.Core.Config;

/// <summary>
/// A local process the service needs running before it has anywhere to load: a policy editor or
/// dashboard that serves itself on a random port and prints where. Muster starts it when the
/// session is created, loads the address it reports, and kills it with the session.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place a config entry runs code, and it was let past the scope list
/// deliberately, on 2026-10-15. It is narrow on purpose: one process per service, owned by that
/// service's session, started and stopped by it and nothing else. It is not a hook system, there
/// is no "on workspace start", and nothing here may grow into one without the same decision.
/// </para>
/// <para>
/// <see cref="ServiceConfig.Url"/> still matters. It names the host the reported address must be
/// on, so a pattern that happens to match some other link in the output cannot send the tab
/// anywhere else. The port, path and query come from the process.
/// </para>
/// </remarks>
public sealed record ServiceLaunchConfig
{
    public const int DefaultTimeoutSeconds = 60;

    public const int MaxTimeoutSeconds = 600;

    /// <summary>The executable, resolved on PATH the way a shell would, <c>.cmd</c> included.</summary>
    public required string Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>
    /// A regular expression run over each line the process writes. The first capture group is the
    /// address to load.
    /// </summary>
    public required string UrlPattern { get; init; }

    /// <summary>
    /// Where the process starts. Defaults to the temp folder, because npx puts the working
    /// directory's <c>node_modules\.bin</c> first on PATH, and a home folder with a stray
    /// <c>node</c> package in it silently swaps in another Node.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>How long to wait for the address before giving up and saying so in the tab.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    // Args is a list, and records compare lists by reference. ConfigDiff decides whether a session
    // has to be rebuilt from this, so a reference comparison would rebuild it on every reload.
    public bool Equals(ServiceLaunchConfig? other)
        => other is not null
        && Command == other.Command
        && Args.SequenceEqual(other.Args)
        && UrlPattern == other.UrlPattern
        && WorkingDirectory == other.WorkingDirectory
        && TimeoutSeconds == other.TimeoutSeconds;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Command);
        hash.Add(UrlPattern);
        hash.Add(WorkingDirectory);
        hash.Add(TimeoutSeconds);

        foreach (var arg in Args)
        {
            hash.Add(arg);
        }

        return hash.ToHashCode();
    }
}
