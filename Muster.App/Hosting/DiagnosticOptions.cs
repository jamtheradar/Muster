namespace Muster.App.Hosting;

/// <summary>
/// Switches for diagnostics that are too invasive to leave on. Set from the command line at
/// startup and never reloaded: a probe that came and went with a config save would be worse than
/// useless for finding an intermittent fault.
/// </summary>
/// <param name="ProbePresence">
/// Injects <c>probe-presence.js</c>, which reports how Teams itself sets your status. Read only,
/// and off by default because it wraps <c>fetch</c> and <c>XMLHttpRequest</c> in a live Teams tab.
/// </param>
public sealed record DiagnosticOptions(bool ProbePresence = false)
{
    public static readonly DiagnosticOptions None = new();

    /// <summary>Reads the diagnostic flags out of the command line.</summary>
    public static DiagnosticOptions FromArgs(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new DiagnosticOptions(
            ProbePresence: args.Any(arg => arg.Equals("--probe-presence", StringComparison.OrdinalIgnoreCase)));
    }
}
