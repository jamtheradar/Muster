namespace Muster.Core.Config;

/// <summary>
/// How the host treats a service. Teams is the only kind that gets bespoke logic, because it is
/// the only one where presence sync applies. Resist adding a third member.
/// </summary>
public enum ServiceKind
{
    /// <summary>Load a URL, parse the title for a count, intercept notifications. That is all.</summary>
    Generic,

    /// <summary>Generic behaviour plus call detection and presence coordination.</summary>
    Teams,
}
