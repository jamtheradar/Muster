namespace Muster.Core.Services;

/// <summary>
/// Whether the thing Windows has registered for http/https is a router that picks a browser
/// profile per URL, rather than an ordinary browser signed into one account.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole question behind <c>links.external: auto</c>, and it is a guess by nature:
/// Windows offers a ProgId and a command line, and no way to ask an application what it does.
/// Both signals are checked because neither is reliable alone. A ProgId is stable but branded —
/// the build installed here registers <c>DataByteUrlRouterURL</c> while the upstream source
/// registers <c>UrlRouterURL</c>, so a single hardcoded name was wrong on the first machine it
/// met. The executable name survives rebranding but is trivially imitated.
/// </para>
/// <para>
/// Wrong in the cautious direction costs a link opening in a tab instead of a browser, which is
/// visible and harmless. Wrong the other way costs a link opening in the wrong identity, so a
/// guess is only allowed to say yes on a real match — never on "it looks like a browser".
/// </para>
/// <para>
/// Here rather than in the host so it can be tested: <c>Muster.Core</c> must not read the
/// registry, so the host reads the two strings and asks this what they mean.
/// </para>
/// </remarks>
public static class ProfileRoutingHandler
{
    /// <summary>
    /// ProgIds known to belong to a profile router. Branded builds register their own, so this is
    /// a list rather than a constant and will grow.
    /// </summary>
    public static IReadOnlyList<string> KnownProgIds { get; } =
    [
        "UrlRouterURL",
        "DataByteUrlRouterURL",
    ];

    /// <summary>
    /// The executable a profile router runs as, whatever ProgId it registered under. Matched on
    /// the file name alone because the install location is the user's choice.
    /// </summary>
    public const string KnownExecutable = "UrlRouter.exe";

    /// <summary>
    /// Whether the registered handler routes by profile. <paramref name="openCommand"/> is the
    /// <c>shell\open\command</c> of the handler, which may be null when it cannot be read.
    /// </summary>
    public static bool Matches(string? progId, string? openCommand)
    {
        if (progId is not null
            && KnownProgIds.Contains(progId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return ExecutableOf(openCommand) is { } executable
            && string.Equals(executable, KnownExecutable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file name of the executable a <c>shell\open\command</c> runs, or null where it cannot
    /// be picked out.
    /// </summary>
    /// <remarks>
    /// Registered commands come in two shapes: quoted, which is what every installer writes
    /// (<c>"C:\...\UrlRouter.exe" --single-argument %1</c>), and bare, which older ones do. Taking
    /// everything up to the first space would truncate the common case at "C:\Program", so the
    /// quoted form is handled first and explicitly.
    /// </remarks>
    public static string? ExecutableOf(string? openCommand)
    {
        if (string.IsNullOrWhiteSpace(openCommand))
        {
            return null;
        }

        var command = openCommand.Trim();

        string path;
        if (command[0] == '"')
        {
            var closing = command.IndexOf('"', 1);
            if (closing < 0)
            {
                return null;
            }

            path = command[1..closing];
        }
        else
        {
            var space = command.IndexOf(' ');
            path = space < 0 ? command : command[..space];
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            // Invalid path characters. Not a command we can make sense of, so not a match.
            return null;
        }
    }
}
