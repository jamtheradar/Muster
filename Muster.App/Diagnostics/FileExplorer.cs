using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Muster.App.Diagnostics;

/// <summary>
/// Hands a folder or file to the Windows shell. Used to make the log folder and muster.json
/// reachable without the user having to know where <c>%LOCALAPPDATA%</c> is.
/// </summary>
/// <remarks>
/// Every method swallows its failures: nothing here is load-bearing, and a shell association that
/// has been broken by some other software is not Muster's problem to crash over.
/// </remarks>
public static class FileExplorer
{
    /// <summary>Opens a folder, creating it first if it has never been written to.</summary>
    public static bool OpenFolder(string directory, ILogger log)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return Start(new ProcessStartInfo(directory) { UseShellExecute = true }, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.LogWarning(ex, "Could not open the folder {Directory}", directory);
            return false;
        }
    }

    /// <summary>Opens the containing folder with the file already selected.</summary>
    public static bool RevealFile(string path, ILogger log)
    {
        if (!File.Exists(path))
        {
            return OpenFolder(Path.GetDirectoryName(path) ?? path, log);
        }

        // The comma is part of the switch, and the path has to be quoted or a space in it splits
        // the argument and Explorer opens Documents instead.
        return Start(
            new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true },
            log);
    }

    /// <summary>Opens a file in whatever is associated with it. Log files go to the text editor.</summary>
    public static bool OpenFile(string path, ILogger log)
    {
        if (!File.Exists(path))
        {
            log.LogInformation("Nothing to open: {Path} does not exist", path);
            return false;
        }

        return Start(new ProcessStartInfo(path) { UseShellExecute = true }, log);
    }

    /// <summary>
    /// Opens a link in whatever the user's browser is. Unlike a link out of a page, this one is
    /// not routed: it is the About screen's own project link, not content, so there is no
    /// identity for it to arrive under the wrong one of.
    /// </summary>
    public static bool OpenLink(Uri target, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(target);

        // The About screen only ever passes its own constants, but this is still the boundary
        // where a string becomes something Windows will launch.
        if (target.Scheme != Uri.UriSchemeHttps)
        {
            log.LogWarning("Refusing to open {Target}: only https links are opened from here", target);
            return false;
        }

        return Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }, log);
    }

    private static bool Start(ProcessStartInfo info, ILogger log)
    {
        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Shell could not open {Target}", info.FileName);
            return false;
        }
    }
}
