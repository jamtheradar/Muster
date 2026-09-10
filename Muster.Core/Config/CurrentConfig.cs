namespace Muster.Core.Config;

/// <summary>
/// The config the app is running on right now, as one injectable object.
/// </summary>
/// <remarks>
/// <para>
/// Before hot-reload the composition root injected <see cref="NotificationConfig"/> and
/// <see cref="PresenceConfig"/> directly, which meant every consumer held a snapshot taken at
/// startup and a saved change could not be honoured without a relaunch. Reading through this
/// instead costs a property hop and makes those settings live.
/// </para>
/// <para>
/// Only whoever owns the reload path may call <see cref="Set"/>: the value here is what the app
/// has actually adopted, not what happens to be on disk. Reads are lock-free and consumers see a
/// whole config or the previous one, never a half-applied mixture, because the record is
/// swapped wholesale.
/// </para>
/// </remarks>
public sealed class CurrentConfig(MusterConfig? initial = null)
{
    private volatile MusterConfig _value = initial ?? new MusterConfig();

    public MusterConfig Value => _value;

    public PresenceConfig Presence => _value.Presence;

    public NotificationConfig Notifications => _value.Notifications;

    public LoggingConfig Logging => _value.Logging;

    /// <summary>Adopts a config. Call only after it has actually been applied.</summary>
    public void Set(MusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _value = config;
    }
}
