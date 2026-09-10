using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Muster.Core.Config;
using Muster.Core.Services;

namespace Muster.App.Hosting;

/// <summary>
/// The one place in the app that hands a URL to Windows, and the one that decides whether it may.
/// </summary>
/// <remarks>
/// <para>
/// The rule everywhere else in the shell is that a link must never reach the system browser,
/// because the system browser is one signed-in identity and the link probably belongs to another.
/// That is an argument about what is registered, not about leaving the app: where the registered
/// http/https handler is a router that picks a browser profile per URL, going out to Windows is
/// how a link reaches the right identity. So this asks Windows what is registered rather than
/// assuming, and <see cref="ExternalLinkPolicy.Auto"/> — the default — answers no on a machine
/// with an ordinary browser as default.
/// </para>
/// <para>
/// Nothing here is cached. The default handler changes from outside the process, under Settings,
/// and a remembered answer would keep sending links to a router the user has since replaced —
/// silently, because the only symptom is a link opening in the wrong place.
/// </para>
/// </remarks>
public sealed class ExternalLinkOpener(CurrentConfig config, ILogger<ExternalLinkOpener> log)
{
    /// <summary>Where Windows records the user's own choice of handler, as opposed to a default.</summary>
    private const string UserChoiceKey =
        @"Software\Microsoft\Windows\CurrentVersion\Shell Associations\UrlAssociations\https\UserChoice";

    /// <summary>Whether a link with no pinned service to go to may leave the shell right now.</summary>
    public bool IsEnabled => config.Value.Links.External switch
    {
        ExternalLinkPolicy.Always => true,
        ExternalLinkPolicy.Auto => RoutesByProfile,
        _ => false,
    };

    /// <summary>
    /// The ProgId the user chose for https, or null when they have never chosen one.
    /// </summary>
    /// <remarks>
    /// Null is a normal state, not an error: on a managed machine the effective handler can come
    /// from the class registration alone, with no UserChoice ever written. That is why
    /// <see cref="OpenCommand"/> exists — the ProgId on its own would report "no idea" on a
    /// machine that plainly does have a browser.
    /// </remarks>
    public static string? DefaultHandlerProgId => ReadValue(Registry.CurrentUser, UserChoiceKey, "ProgId");

    /// <summary>
    /// What Windows would actually run for an https URL, as a command line, or null if it cannot
    /// be read.
    /// </summary>
    /// <remarks>
    /// Resolved the way the shell does: the chosen ProgId's own command if there is one, and the
    /// registration for the protocol itself otherwise. Reading only the first would miss a
    /// perfectly ordinary machine; reading only the second would miss the user's choice.
    /// </remarks>
    public static string? OpenCommand
    {
        get
        {
            if (DefaultHandlerProgId is { } progId)
            {
                var command = ReadValue(Registry.CurrentUser, $@"SOFTWARE\Classes\{progId}\shell\open\command", null)
                    ?? ReadValue(Registry.ClassesRoot, $@"{progId}\shell\open\command", null);

                if (command is not null)
                {
                    return command;
                }
            }

            return ReadValue(Registry.ClassesRoot, @"https\shell\open\command", null);
        }
    }

    /// <summary>True when the registered handler is one this build knows routes by profile.</summary>
    public bool RoutesByProfile => ProfileRoutingHandler.Matches(DefaultHandlerProgId, OpenCommand);

    /// <summary>
    /// What is registered, in the words a person would use. Reported rather than assumed, for the
    /// same reason URL Router's own setup tab reports it: an application cannot make itself the
    /// default browser, so the only honest thing to show is what Windows actually says.
    /// </summary>
    public static string DescribeHandler()
    {
        var progId = DefaultHandlerProgId;
        var executable = ProfileRoutingHandler.ExecutableOf(OpenCommand);

        var named = (progId, executable) switch
        {
            (null, null) => "Windows has not recorded a handler for https",
            (null, var exe) => $"Windows opens https links with {exe}",
            (var id, null) => $"Windows opens https links with '{id}'",
            var (id, exe) => $"Windows opens https links with {exe} (registered as '{id}')",
        };

        return ProfileRoutingHandler.Matches(progId, executable is null ? null : OpenCommand)
            ? $"{named}, which routes each link to the browser profile its rules name. Auto lets links leave Muster."
            : $"{named}, which this build does not know to route by profile. Auto keeps every link inside Muster.";
    }

    /// <summary>
    /// Hands <paramref name="target"/> to Windows. False means nothing was opened and the caller
    /// still owns the link — the shell falls back to a tab rather than dropping it.
    /// </summary>
    public bool TryOpen(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Whatever routed us here, this is the boundary where a URL stops being ours. A scheme
        // check at the boundary is what stops a page turning an external link into a protocol
        // handler launch.
        if (!UrlNormaliser.IsSupportedScheme(target))
        {
            log.LogWarning("Refusing to open {Uri} externally: not an http or https URL", target);
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(target.AbsoluteUri)
            {
                UseShellExecute = true,
            });

            log.LogInformation(
                "Opened {Uri} externally via {Handler}",
                target,
                DefaultHandlerProgId ?? ProfileRoutingHandler.ExecutableOf(OpenCommand) ?? "the registered handler");

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            log.LogError(ex, "Could not open {Uri} externally", target);
            return false;
        }
    }

    /// <summary>
    /// A registry read that answers null rather than throwing. Not knowing what is registered has
    /// to be an ordinary answer here: under Auto it means no, which is the safe direction.
    /// </summary>
    private static string? ReadValue(RegistryKey root, string path, string? name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
