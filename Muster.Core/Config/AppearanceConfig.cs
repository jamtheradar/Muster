namespace Muster.Core.Config;

/// <summary>
/// How the shell itself looks, as opposed to how a workspace does. Workspace accents stay on the
/// workspace: those identify a tenant, and this only decides which colourway of the app's own mark
/// is on screen.
/// </summary>
public sealed record AppearanceConfig
{
    /// <summary>Which mark the tray icon, the windows and the toast sender wear.</summary>
    public IconSet IconSet { get; init; } = IconSet.Slate;
}
