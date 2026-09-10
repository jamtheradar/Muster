using Microsoft.Extensions.Logging;
using Muster.Core.Presence;

namespace Muster.App.Logging;

/// <summary>
/// Gives a strategy in <c>Muster.Core</c> somewhere to write, without Core taking a logging
/// dependency it has managed without everywhere else.
/// </summary>
/// <remarks>
/// The category is the strategy's own name rather than this adapter's, so a line about presence
/// reads as coming from presence. Strategies pass labels and status codes; nothing that reaches
/// here is ever a header or a token.
/// </remarks>
public sealed class PresenceStrategyLog(ILoggerFactory factory) : IPresenceStrategyLog
{
    private readonly ILogger _log = factory.CreateLogger("Presence");

    public void Info(string message) => _log.LogInformation("{Message}", message);

    public void Warn(string message) => _log.LogWarning("{Message}", message);
}
