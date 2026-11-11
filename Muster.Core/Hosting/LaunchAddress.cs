using System.Text.RegularExpressions;
using Muster.Core.Services;

namespace Muster.Core.Hosting;

/// <summary>
/// Reads the address a launched process reports out of its output. Kept free of any process type
/// so the rules about what it will and will not accept are unit tests.
/// </summary>
public static class LaunchAddress
{
    /// <summary>
    /// The pattern comes from a hand-edited file and runs over every line a process writes, so a
    /// catastrophic one has to fail a line rather than hang the UI thread.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Compiles a configured pattern. Throws <see cref="ArgumentException"/> if it is not one.</summary>
    public static Regex Compile(string pattern) => new(pattern, RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>
    /// The address in <paramref name="line"/>, if the pattern's first group captures one that is on
    /// the same host as <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// The host check is the point. A pattern loose enough to match a documentation link or an
    /// update notice in the output would otherwise send the tab there, in a profile that may be
    /// signed in to something. The port is not checked, because choosing it is the process's job.
    /// </remarks>
    public static bool TryRead(Regex pattern, string? line, Uri expected, out Uri? address)
    {
        address = null;

        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        Match match;
        try
        {
            match = pattern.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success || match.Groups.Count < 2 || !match.Groups[1].Success)
        {
            return false;
        }

        if (!Uri.TryCreate(match.Groups[1].Value.Trim(), UriKind.Absolute, out var candidate)
            || !UrlNormaliser.IsSupportedScheme(candidate)
            || !string.Equals(candidate.Host, expected.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        address = candidate;
        return true;
    }

    /// <summary>
    /// The address without its query, for the log. Local servers routinely put their access token
    /// there, and the log is a file the user is encouraged to open and share.
    /// </summary>
    public static string Redacted(Uri address) => address.GetLeftPart(UriPartial.Path);
}
