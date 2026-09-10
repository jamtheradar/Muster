namespace Muster.Core.Config;

/// <summary>
/// The whole of <c>%APPDATA%\Muster\muster.json</c>. Hand-editable by design.
/// </summary>
public sealed record MusterConfig
{
    /// <summary>Schema version. Bump only for a breaking shape change, with a migration.</summary>
    public int Version { get; init; } = CurrentVersion;

    public IReadOnlyList<WorkspaceConfig> Workspaces { get; init; } = [];

    public PresenceConfig Presence { get; init; } = new();

    public NotificationConfig Notifications { get; init; } = new();

    public AppearanceConfig Appearance { get; init; } = new();

    public LinkConfig Links { get; init; } = new();

    public SuspensionConfig Suspension { get; init; } = new();

    public LoggingConfig Logging { get; init; } = new();

    public const int CurrentVersion = 1;

    /// <summary>Finds a service by id across every workspace.</summary>
    public ServiceConfig? FindService(string serviceId)
        => Workspaces.SelectMany(w => w.Services).FirstOrDefault(s => s.Id == serviceId);

    /// <summary>Finds the workspace that owns a given service.</summary>
    public WorkspaceConfig? FindWorkspaceOfService(string serviceId)
        => Workspaces.FirstOrDefault(w => w.Services.Any(s => s.Id == serviceId));

    // See the note in WorkspaceConfig: collection members need structural equality.
    public bool Equals(MusterConfig? other)
        => other is not null
        && Version == other.Version
        && Presence == other.Presence
        && Notifications == other.Notifications
        && Appearance == other.Appearance
        && Links == other.Links
        && Suspension == other.Suspension
        && Logging == other.Logging
        && Workspaces.SequenceEqual(other.Workspaces);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(Presence);
        hash.Add(Notifications);
        hash.Add(Appearance);
        hash.Add(Links);
        hash.Add(Suspension);
        hash.Add(Logging);
        foreach (var workspace in Workspaces)
        {
            hash.Add(workspace);
        }

        return hash.ToHashCode();
    }
}
