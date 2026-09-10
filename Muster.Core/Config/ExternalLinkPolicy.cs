namespace Muster.Core.Config;

/// <summary>
/// Whether a link belonging to no pinned service may leave the shell for whatever Windows has
/// registered as the browser. See <c>NewWindowRouter</c>.
/// </summary>
/// <remarks>
/// The default is <see cref="Auto"/> rather than <see cref="Never"/> because the whole question
/// turns on what is registered: handing a link to a single signed-in Edge is the problem this app
/// exists to solve, and handing it to a router that picks a profile per URL is the answer to it.
/// Guessing wrong in either direction is worse than asking Windows which one is installed.
/// </remarks>
public enum ExternalLinkPolicy
{
    /// <summary>
    /// Every link stays inside Muster, as it did before this setting existed. The right answer
    /// where the default browser is an ordinary one signed into a single account.
    /// </summary>
    Never,

    /// <summary>
    /// External, but only where the registered http/https handler is known to route by profile.
    /// The host resolves this and tells the router the answer; nothing in <c>Muster.Core</c> reads
    /// the registry.
    /// </summary>
    Auto,

    /// <summary>
    /// External whatever is registered. For a machine where the handler routes by profile under a
    /// name this build does not recognise — a fork of the router, or a different tool entirely.
    /// It is an assertion by the user that leaving the shell is safe, so it is not the default.
    /// </summary>
    Always,
}
