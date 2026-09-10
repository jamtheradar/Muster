using Microsoft.Extensions.Logging;
using Muster.App.Logging;
using Muster.Core.Config;
using Muster.Core.Diagnostics;
using Muster.Core.Hosting;
using Muster.Core.Notifications;
using Muster.Core.Presence;
using Muster.Graph;

namespace Muster.App.Configuration;

/// <summary>
/// Pushes the settings that are not workspaces into the running app: everything under
/// <c>presence</c>, <c>notifications</c> and <c>logging</c> that can be honoured without rebuilding
/// a session.
/// </summary>
/// <remarks>
/// The one place that knows how to adopt a config, used both at startup and on every reload, so
/// the two cannot drift. Whether a setting reaches here at all is <see cref="ConfigDiff"/>'s
/// decision; what to do with it is this class's.
/// </remarks>
public sealed class LiveSettings(
    CurrentConfig current,
    AppIcons icons,
    NotificationHub hub,
    PresenceCoordinator presence,
    PresenceApplier applier,
    GraphPresenceOptions graph,
    SuspensionCoordinator suspension,
    FileLoggerProvider logs,
    ILogger<LiveSettings> log)
{
    /// <summary>
    /// Adopts <paramref name="config"/>. Safe to call with a config that has not changed, so the
    /// caller does not have to work out whether it needs to.
    /// </summary>
    public void Apply(MusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        current.Set(config);

        // Toasts read through CurrentConfig at the point of use, so toastsEnabled is already live
        // by the line above. The rest hold state that has to be told.
        hub.HistoryLimit = config.Notifications.HistoryLimit;

        // Everything that draws the mark listens to this and repaints itself. Called on the
        // dispatcher, as every caller of Apply is, because two of those listeners are windows.
        icons.Apply(config.Appearance);

        presence.Apply(config.Presence);

        // The applier only adopts the settings here. Writing is the caller's next step, because
        // an edit that removes an account has to clear it, and clearing is a network round trip
        // that has no business happening inside a settings apply.
        graph.ClientId = config.Presence.ClientId;
        applier.Apply(config);

        suspension.Apply(config.Suspension);
        logs.MinimumLevel = config.Logging.Level;

        // The folder cannot move without a restart, but the retention window applies to the one
        // being written now.
        var swept = LogFolder.Prune(logs.Directory, config.Logging.RetentionDays, DateTimeOffset.Now);

        log.LogInformation(
            "Applied settings: log level {Level}, history {History}, icons {Icons}, " +
            "presence {Presence} {Detect}s/{Clear}s over {Accounts} account(s), " +
            "suspension {Suspension}, {Swept} expired log file(s) removed",
            config.Logging.Level,
            config.Notifications.HistoryLimit,
            config.Appearance.IconSet,
            config.Presence.Enabled ? "on" : "off",
            config.Presence.CallDetectDelaySeconds,
            config.Presence.CallClearDelaySeconds,
            applier.Accounts.Count,
            config.Suspension.Enabled ? $"after {config.Suspension.IdleMinutes}m idle" : "off",
            swept);
    }
}
