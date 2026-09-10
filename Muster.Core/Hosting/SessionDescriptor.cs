using Muster.Core.Config;

namespace Muster.Core.Hosting;

/// <summary>
/// Everything the host needs to stand up one live web session, flattened out of the config tree
/// so the view layer never has to walk it.
/// </summary>
/// <param name="Id">Stable session id. For a pinned service this is the service id.</param>
/// <param name="WorkspaceId">The owning workspace, used for badge aggregation and accents.</param>
/// <param name="Name">Display name for the tab.</param>
/// <param name="ProfileName">Resolved WebView2 profile name.</param>
/// <param name="Home">The URL loaded on first navigation.</param>
public sealed record SessionDescriptor(
    string Id,
    string WorkspaceId,
    string Name,
    string ProfileName,
    Uri Home)
{
    /// <summary>
    /// True for a transient tab opened from a link or the address bar. Ephemeral sessions are
    /// never written to config and do not survive a restart.
    /// </summary>
    public bool IsEphemeral { get; init; }

    /// <summary>
    /// Host suffixes this session grants media and notification permissions to without asking,
    /// from the owning workspace. See SPEC section 6.3.
    /// </summary>
    public IReadOnlyList<string> AllowedPermissionOrigins { get; init; } = [];

    /// <summary>
    /// Never suspended, and brought up at launch rather than on first visit. Unlike the profile
    /// and the permission origins this can change under a running session: it only decides
    /// whether the session is a suspension candidate, which is asked fresh each time.
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>
    /// True for a session that lives in its own top-level window rather than in the tab strip.
    /// Floating sessions take no part in tab switching and never appear on the rail.
    /// </summary>
    public bool IsFloating { get; init; }

    /// <summary>Builds the descriptor for a pinned service.</summary>
    public static SessionDescriptor ForService(WorkspaceConfig workspace, ServiceConfig service) => new(
        Id: service.Id,
        WorkspaceId: workspace.Id,
        Name: service.Name,
        ProfileName: service.ResolveProfileName(workspace),
        Home: service.Url)
    {
        AllowedPermissionOrigins = workspace.PermissionOrigins,
        KeepAlive = service.KeepAlive,
    };

    /// <summary>
    /// Builds the descriptor for an ephemeral tab. It runs in the workspace's own profile, so the
    /// identity carries across from whatever opened it.
    /// </summary>
    public static SessionDescriptor ForEphemeral(
        string workspaceId,
        string profileName,
        Uri target,
        IReadOnlyList<string>? allowedPermissionOrigins = null) => new(
        Id: $"eph-{Guid.NewGuid():N}",
        WorkspaceId: workspaceId,
        Name: DisplayNameFor(target),
        ProfileName: profileName,
        Home: target)
    {
        IsEphemeral = true,
        AllowedPermissionOrigins = allowedPermissionOrigins ?? [],
    };

    /// <summary>
    /// Builds the descriptor for a popped-out window: a Teams meeting on a second monitor, or a
    /// sign-in prompt. It runs in the workspace's own profile, so the identity carries across.
    /// </summary>
    /// <remarks>
    /// Marked <see cref="KeepAlive"/> because a floating window is on screen whatever the rail
    /// says. Its workspace is not the active one for most of the time it is open, which without
    /// this would make it a suspension candidate — a meeting put to sleep while you are watching
    /// it.
    /// </remarks>
    public static SessionDescriptor ForPopup(
        string workspaceId,
        string profileName,
        Uri target,
        IReadOnlyList<string>? allowedPermissionOrigins = null) => new(
        Id: $"pop-{Guid.NewGuid():N}",
        WorkspaceId: workspaceId,
        Name: DisplayNameFor(target),
        ProfileName: profileName,
        Home: target)
    {
        IsEphemeral = true,
        IsFloating = true,
        KeepAlive = true,
        AllowedPermissionOrigins = allowedPermissionOrigins ?? [],
    };

    /// <summary>A short label for a tab before the page reports a title.</summary>
    public static string DisplayNameFor(Uri target)
    {
        if (!target.IsAbsoluteUri)
        {
            return target.ToString();
        }

        var host = target.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }
}
