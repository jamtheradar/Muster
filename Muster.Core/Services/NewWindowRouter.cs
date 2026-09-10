using Muster.Core.Hosting;

namespace Muster.Core.Services;

/// <summary>
/// Decides where a <c>NewWindowRequested</c> goes.
/// </summary>
/// <remarks>
/// This used to answer "never the system browser", full stop, because the system browser means
/// one arbitrary identity and a link landing in the wrong one is the problem this app exists to
/// solve. That holds only while the registered handler is an ordinary browser. Where it is a
/// router that picks a browser profile per URL, going out to Windows is how a link reaches the
/// right identity rather than how it loses it — so the decision moved to the host, which can see
/// what is registered, and arrives here as <c>allowExternal</c>. With it false, this behaves
/// exactly as it always did.
/// </remarks>
public static class NewWindowRouter
{
    /// <summary>
    /// Picks a route for <paramref name="target"/> against the services pinned in the workspace
    /// the request came from.
    /// </summary>
    /// <param name="pinned">Pinned services of the requesting workspace, in tab order.</param>
    /// <param name="target">The URL the page wants to open, exactly as the page gave it.</param>
    /// <param name="isScriptedPopup">
    /// True when the page called <c>window.open</c> with window features, as opposed to following
    /// a <c>target=_blank</c> link. Scripted popups always get their own session: Entra sign-in
    /// arrives this way and needs a live <c>window.opener</c>, so reusing a pinned tab would both
    /// break the auth handshake and throw away that tab's state.
    /// </param>
    /// <param name="allowExternal">
    /// Whether a link matching nothing pinned may leave the shell. The host answers this from
    /// <c>links.external</c> and what Windows has registered; nothing here reads either.
    /// </param>
    /// <remarks>
    /// A sized popup becomes a floating window rather than a tab. Asking for width and height is
    /// a page saying it wants a window, and the two things that arrive this way both need to be
    /// one: a popped-out Teams meeting is useless if it cannot be dragged to a second monitor,
    /// and a sign-in prompt reads as a hijacked tab when it appears in the tab strip. Both still
    /// run in this workspace's profile, so the identity model is untouched — the window is ours,
    /// not the system browser's.
    /// </remarks>
    public static NewWindowRoute Route(
        IEnumerable<SessionDescriptor> pinned,
        Uri target,
        bool isScriptedPopup,
        bool allowExternal = false)
    {
        if (isScriptedPopup)
        {
            // Never external, whatever the policy says. A sign-in popup handed to another browser
            // is a sign-in that completes somewhere the page cannot see, leaving the tab that
            // asked for it waiting on a window.opener that will never report back.
            return new NewWindowRoute.OpenFloatingWindow();
        }

        // Matched against the destination, not the wrapper. Where the tenant has Safe Links on,
        // every link in a Teams message wears the same safelinks host, so matching what the page
        // handed us sends a link to a service pinned in this very workspace to a throwaway tab.
        // Only the decision uses this; the caller still navigates the wrapped URL.
        var destination = SafeLinks.Unwrap(target);

        var match = pinned.FirstOrDefault(service => IsSameOrigin(service.Home, destination));

        if (match is not null)
        {
            return new NewWindowRoute.ActivatePinnedService(match.Id);
        }

        return allowExternal
            ? new NewWindowRoute.OpenExternally()
            : new NewWindowRoute.OpenEphemeralTab();
    }

    /// <summary>Scheme, host and port must all match. Path and query are ignored.</summary>
    public static bool IsSameOrigin(Uri left, Uri right)
        => left.IsAbsoluteUri
        && right.IsAbsoluteUri
        && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}
