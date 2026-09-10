using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muster.App.Configuration;
using Muster.Core.Config;

namespace Muster.App.Hosting;

/// <summary>
/// Points the pinned taskbar shortcut at the chosen icon set. The one surface the setting cannot
/// otherwise reach, short of rebuilding the exe.
/// </summary>
/// <remarks>
/// <para>
/// A pinned taskbar button is drawn from the <c>.lnk</c>'s icon location, not from the running
/// window, so it keeps showing whatever the shortcut was made with — normally <c>Muster.exe,0</c>,
/// the icon <c>ApplicationIcon</c> compiled in. Embedding all eight in the exe would not help by
/// itself: the shortcut would still have to be repointed at an index. So the shortcut is what gets
/// edited, and it is aimed at the <c>.ico</c> beside the exe — the same file the tray and the
/// windows load.
/// </para>
/// <para>
/// Driven by a button rather than by saving the config. Editing a file in the user's taskbar
/// folder is outward-facing in a way that changing a colour is not, and the previous value goes to
/// the log so the change can be undone by hand.
/// </para>
/// <para>
/// This only fixes a pin that already exists, and measurably does not make the taskbar redraw:
/// with the link pointing at the new <c>.ico</c> and the window's big and small icons both
/// changed, the button still showed the old mark, and neither <c>SHCNE_UPDATEITEM</c>,
/// <c>SHCNE_ASSOCCHANGED</c> nor <c>ie4uinit -show</c> moved it. The taskbar caches a pinned
/// item's icon in its own state and re-reads on an Explorer restart.
/// </para>
/// <para>
/// So repinning is the practical remedy, not the hazard it first looked like — but only because
/// <see cref="RelaunchProperties"/> makes the replacement shortcut come out with the right icon.
/// Without those properties a repin reverts to the compiled-in mark, which is exactly what
/// happened before they existed. The two classes are halves of one answer.
/// </para>
/// <para>
/// Written through <c>WScript.Shell</c>, in-box on every Windows, rather than a hand-declared
/// <c>IShellLink</c> vtable. Both reach the same shell API; this one cannot be got subtly wrong in
/// a way that corrupts a shortcut. The link is read back afterwards, because a save that silently
/// did nothing is the failure worth catching.
/// </para>
/// </remarks>
public sealed partial class PinnedShortcut(ILogger<PinnedShortcut> log)
{
    /// <summary><c>%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar</c>.</summary>
    public static string TaskBarFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Internet Explorer",
        "Quick Launch",
        "User Pinned",
        "TaskBar");

    /// <summary>
    /// Repoints whichever pinned shortcut launches this exe at the icon for <paramref name="set"/>.
    /// Never throws: the outcome is the returned message, because every step here is something the
    /// machine is entitled to refuse.
    /// </summary>
    public string Apply(IconSet set)
    {
        var icon = AppIcons.PathOf(set);

        if (!File.Exists(icon))
        {
            log.LogWarning("Cannot repoint the pinned shortcut: {Icon} is missing", icon);
            return $"{Path.GetFileName(icon)} is not beside the exe, so nothing was changed.";
        }

        string? shortcut;

        try
        {
            shortcut = Find();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read {Folder}", TaskBarFolder);
            return "The taskbar folder could not be read.";
        }

        if (shortcut is null)
        {
            return "Muster is not pinned to the taskbar, so there is no shortcut to change. " +
                   "Pin it first, then use this again.";
        }

        try
        {
            var previous = IconLocationOf(shortcut);
            SetIconLocation(shortcut, icon);
            var now = IconLocationOf(shortcut);

            if (!now.StartsWith(icon, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning(
                    "Wrote {Icon} to {Shortcut} but it reads back as {Now}", icon, shortcut, now);

                return "The shortcut did not keep the new icon. See the log.";
            }

            log.LogInformation(
                "Pinned shortcut {Shortcut} icon changed from {Previous} to {Now}",
                shortcut,
                previous,
                now);

            Notify(shortcut);

            return $"Pointed {Path.GetFileName(shortcut)} at {Path.GetFileName(icon)}. The taskbar " +
                   "will keep drawing the old one until Explorer restarts - it caches pinned icons " +
                   "and ignores both the shortcut and the window. Unpinning and repinning shows the " +
                   "new icon straight away instead.";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not repoint the pinned shortcut {Shortcut}", shortcut);
            return $"Could not change {Path.GetFileName(shortcut)}: {ex.Message}";
        }
    }

    /// <summary>What the pinned shortcut draws its icon from now, or null when there is not one.</summary>
    public string? Describe()
    {
        try
        {
            if (Find() is not { } shortcut)
            {
                return null;
            }

            return $"Pinned as {Path.GetFileName(shortcut)}, drawing its icon from " +
                   $"{IconLocationOf(shortcut)}.";
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not describe the pinned shortcut");
            return null;
        }
    }

    /// <summary>
    /// The pinned shortcut that launches this exe, matched on its target rather than its name: a
    /// pin can be renamed, and more than one build can be pinned at once.
    /// </summary>
    private string? Find()
    {
        var self = Environment.ProcessPath;

        if (string.IsNullOrEmpty(self) || !Directory.Exists(TaskBarFolder))
        {
            return null;
        }

        foreach (var candidate in Directory.EnumerateFiles(TaskBarFolder, "*.lnk"))
        {
            try
            {
                if (string.Equals(TargetOf(candidate), self, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                // Somebody else's shortcut that this cannot read is not a failure.
                log.LogDebug(ex, "Skipped the pinned shortcut {Shortcut}", candidate);
            }
        }

        return null;
    }

    private static string TargetOf(string shortcut) => Read(shortcut, "TargetPath");

    private static string IconLocationOf(string shortcut) => Read(shortcut, "IconLocation");

    private static string Read(string shortcut, string property)
    {
        var (shell, link) = Open(shortcut);

        try
        {
            return link.GetType().InvokeMember(
                property, BindingFlags.GetProperty, null, link, null) as string ?? string.Empty;
        }
        finally
        {
            Release(shell, link);
        }
    }

    private static void SetIconLocation(string shortcut, string icon)
    {
        var (shell, link) = Open(shortcut);

        try
        {
            var type = link.GetType();

            // Index 0: these are single-image files, not an exe carrying several.
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, [icon + ", 0"]);
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            Release(shell, link);
        }
    }

    private static (object Shell, object Link) Open(string shortcut)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not registered on this machine.");

        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("WScript.Shell could not be created.");

        var link = shellType.InvokeMember(
            "CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcut])
            ?? throw new InvalidOperationException($"{shortcut} could not be opened.");

        return (shell, link);
    }

    private static void Release(object shell, object link)
    {
        if (Marshal.IsComObject(link))
        {
            Marshal.FinalReleaseComObject(link);
        }

        if (Marshal.IsComObject(shell))
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Tells the shell the file changed. Not enough on its own, because the taskbar keeps its own
    /// cache of pinned icons, but it is the difference between Explorer noticing now and noticing
    /// at next sign-in.
    /// </summary>
    private static void Notify(string shortcut)
    {
        const uint UpdateItem = 0x00002000;
        const uint PathW = 0x0005;

        SHChangeNotify(UpdateItem, PathW, shortcut, null);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(uint eventId, uint flags, string? item1, string? item2);
}
