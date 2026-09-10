namespace Muster.Core.Config;

/// <summary>Where links that leave a page are allowed to end up.</summary>
/// <remarks>
/// A section of its own rather than a bare property on <see cref="MusterConfig"/>, so it reads
/// the way the rest of the file does and has somewhere to grow. It has non-null defaults and is
/// therefore always written: the file is hand-edited, and an option you cannot see is an option
/// you will not find.
/// </remarks>
public sealed record LinkConfig
{
    /// <summary>
    /// Whether a link matching no pinned service in its workspace may be handed to Windows.
    /// Scripted popups are never eligible whatever this says — see <c>NewWindowRouter</c>.
    /// </summary>
    public ExternalLinkPolicy External { get; init; } = ExternalLinkPolicy.Auto;
}
