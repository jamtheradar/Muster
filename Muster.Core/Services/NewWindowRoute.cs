namespace Muster.Core.Services;

/// <summary>
/// Where a page's request for a new window should end up. A closed set, so the whole hierarchy
/// lives in one file rather than three.
/// </summary>
public abstract record NewWindowRoute
{
    private NewWindowRoute()
    {
    }

    /// <summary>The URL belongs to a service already pinned in this workspace. Reuse its tab.</summary>
    public sealed record ActivatePinnedService(string ServiceId) : NewWindowRoute;

    /// <summary>
    /// A real top-level window, in the current workspace's profile. This is what a page asking
    /// for a sized popup actually wants: a Teams meeting popped out onto a second monitor is not
    /// a tab, and neither is an Entra sign-in prompt.
    /// </summary>
    public sealed record OpenFloatingWindow : NewWindowRoute;

    /// <summary>
    /// Out of the shell entirely, to whatever Windows has registered for http/https.
    /// </summary>
    /// <remarks>
    /// The one route that leaves the app, and only ever chosen when the host has said it is safe
    /// to — which means the registered handler routes each URL to a browser profile rather than
    /// dropping all of them into one signed-in window. Where that is true this is not the
    /// system-browser fall-through the app exists to prevent: it is the way to get a link to the
    /// identity it belongs to, for a service nobody wanted to pin.
    /// </remarks>
    public sealed record OpenExternally : NewWindowRoute;

    /// <summary>Anything else: a transient tab in the current workspace's profile.</summary>
    public sealed record OpenEphemeralTab : NewWindowRoute;
}
