using Muster.Core.Hosting;

namespace Muster.Core.Config;

/// <summary>
/// What changed between two loads of <c>muster.json</c>, expressed as the actions the host has to
/// take. Hot-reload exists so that editing a colour does not sign you out of five tenants, and
/// that only works if the diff is precise about which of the live sessions actually have to go.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately in <c>Muster.Core</c> and free of any host type, so every rule below is a unit
/// test rather than something you find out about by watching a Teams tab reload.
/// </para>
/// <para>
/// Sessions are keyed by service id, which is what <c>SessionManager</c> keys them by too. A
/// service that keeps its id keeps its live WebView2, and therefore its auth, its notification
/// stream and its place in a call. Everything here is in service of that.
/// </para>
/// </remarks>
public sealed record ConfigDiff
{
    /// <summary>Services in the new config that were not in the old one.</summary>
    public IReadOnlyList<string> AddedServiceIds { get; init; } = [];

    /// <summary>Services that are gone. Their sessions must be torn down.</summary>
    public IReadOnlyList<string> RemovedServiceIds { get; init; } = [];

    /// <summary>
    /// Services whose session cannot be adjusted in place and has to be built again. The profile
    /// name is fixed when the CoreWebView2 controller is created and the permission allow list is
    /// read out of the descriptor, so neither can be changed on a running session.
    /// </summary>
    public IReadOnlyList<string> RecreatedServiceIds { get; init; } = [];

    /// <summary>
    /// Services whose URL changed and nothing else. The session survives — this is a navigation,
    /// which keeps the cookie jar and so keeps the user signed in.
    /// </summary>
    public IReadOnlyList<string> NavigatedServiceIds { get; init; } = [];

    /// <summary>
    /// True if anything at all in the workspace tree differs, cosmetics and ordering included.
    /// The rail and tab strip are rebuilt from this; the session lists above decide what that
    /// rebuild is allowed to disturb.
    /// </summary>
    public bool WorkspacesChanged { get; init; }

    public bool PresenceChanged { get; init; }

    public bool NotificationsChanged { get; init; }

    /// <summary>
    /// Costs no session, and no restart either. Every surface the icon set reaches — the tray, the
    /// window icons, the toast sender — can be repainted underneath a running shell, which is the
    /// only reason a cosmetic setting is worth hot-reloading at all.
    /// </summary>
    public bool AppearanceChanged { get; init; }

    /// <summary>
    /// Costs no session. The policy is read per click rather than held anywhere, so a change here
    /// is live the moment the file lands.
    /// </summary>
    public bool LinksChanged { get; init; }

    public bool SuspensionChanged { get; init; }

    public bool LoggingChanged { get; init; }

    /// <summary>
    /// Why a relaunch is still needed, in the words the settings screen shows. Empty is the
    /// normal case: only the log directory and a schema version bump land here.
    /// </summary>
    public IReadOnlyList<string> RestartReasons { get; init; } = [];

    /// <summary>Nothing changed, so there is nothing to apply.</summary>
    public static readonly ConfigDiff None = new();

    public bool RequiresRestart => RestartReasons.Count > 0;

    public bool IsEmpty => !WorkspacesChanged
        && !PresenceChanged
        && !NotificationsChanged
        && !AppearanceChanged
        && !LinksChanged
        && !SuspensionChanged
        && !LoggingChanged;

    /// <summary>
    /// Every session that has to stop, in one list. A recreated service stops the same way a
    /// removed one does; the difference is only whether it is expected back.
    /// </summary>
    public IEnumerable<string> StoppedServiceIds => RemovedServiceIds.Concat(RecreatedServiceIds);

    /// <summary>Works out what has to happen to get from <paramref name="previous"/> to <paramref name="current"/>.</summary>
    public static ConfigDiff Between(MusterConfig previous, MusterConfig current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var before = Flatten(previous);
        var after = Flatten(current);

        var beforeById = before.ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);
        var afterById = after.ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);

        var added = new List<string>();
        var recreated = new List<string>();
        var navigated = new List<string>();

        // Config order, not dictionary order, so logs and test failures read the way the file does.
        foreach (var descriptor in after)
        {
            if (!beforeById.TryGetValue(descriptor.Id, out var was))
            {
                added.Add(descriptor.Id);
            }
            else if (NeedsRecreation(was, descriptor))
            {
                recreated.Add(descriptor.Id);
            }
            else if (was.Home != descriptor.Home)
            {
                navigated.Add(descriptor.Id);
            }
        }

        var removed = before
            .Where(descriptor => !afterById.ContainsKey(descriptor.Id))
            .Select(descriptor => descriptor.Id)
            .ToList();

        return new ConfigDiff
        {
            AddedServiceIds = added,
            RemovedServiceIds = removed,
            RecreatedServiceIds = recreated,
            NavigatedServiceIds = navigated,
            WorkspacesChanged = !previous.Workspaces.SequenceEqual(current.Workspaces),
            PresenceChanged = previous.Presence != current.Presence,
            NotificationsChanged = previous.Notifications != current.Notifications,
            AppearanceChanged = previous.Appearance != current.Appearance,
            LinksChanged = previous.Links != current.Links,
            SuspensionChanged = previous.Suspension != current.Suspension,
            LoggingChanged = previous.Logging != current.Logging,
            RestartReasons = RestartReasonsFor(previous, current),
        };
    }

    /// <summary>
    /// The two things a running session cannot be talked out of, plus the workspace it belongs to.
    /// </summary>
    /// <remarks>
    /// A service that moves between workspaces usually changes profile as well, so it would be
    /// caught anyway. It is listed explicitly because the case where it does not — two workspaces
    /// pinned to the same explicit <c>profileName</c> — would otherwise leave a live session
    /// carrying its old workspace id into permission checks and notification attribution.
    /// </remarks>
    private static bool NeedsRecreation(SessionDescriptor was, SessionDescriptor now)
        => !string.Equals(was.ProfileName, now.ProfileName, StringComparison.Ordinal)
        || !string.Equals(was.WorkspaceId, now.WorkspaceId, StringComparison.Ordinal)
        || !was.AllowedPermissionOrigins.SequenceEqual(now.AllowedPermissionOrigins, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> RestartReasonsFor(MusterConfig previous, MusterConfig current)
    {
        var reasons = new List<string>();

        // The log file handle is opened once at startup and the folder is resolved with it. Moving
        // it live would either lose this run's lines or leave two writers on one folder.
        if (!string.Equals(previous.Logging.Directory, current.Logging.Directory, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("logging: the log folder is chosen at startup, so a restart is needed to move it.");
        }

        // A schema bump means a migration ran, or is about to. Not something to apply underneath a
        // running shell.
        if (previous.Version != current.Version)
        {
            reasons.Add($"config: schema version changed from {previous.Version} to {current.Version}.");
        }

        return reasons;
    }

    /// <summary>
    /// Every pinned service as the descriptor the host would actually build for it. Going through
    /// <see cref="SessionDescriptor.ForService"/> rather than reading the config members directly
    /// is the point: the diff and the session then resolve profiles and permission origins by the
    /// same rules, and cannot drift apart.
    /// </summary>
    private static IReadOnlyList<SessionDescriptor> Flatten(MusterConfig config) => config.Workspaces
        .SelectMany(workspace => workspace.Services
            .Select(service => SessionDescriptor.ForService(workspace, service)))
        .ToList();

    // Collection members again. See the note in WorkspaceConfig: a diff that compares equal
    // regardless of which services it names would make every test below pass for free.
    public bool Equals(ConfigDiff? other)
        => other is not null
        && WorkspacesChanged == other.WorkspacesChanged
        && PresenceChanged == other.PresenceChanged
        && NotificationsChanged == other.NotificationsChanged
        && AppearanceChanged == other.AppearanceChanged
        && LinksChanged == other.LinksChanged
        && SuspensionChanged == other.SuspensionChanged
        && LoggingChanged == other.LoggingChanged
        && AddedServiceIds.SequenceEqual(other.AddedServiceIds)
        && RemovedServiceIds.SequenceEqual(other.RemovedServiceIds)
        && RecreatedServiceIds.SequenceEqual(other.RecreatedServiceIds)
        && NavigatedServiceIds.SequenceEqual(other.NavigatedServiceIds)
        && RestartReasons.SequenceEqual(other.RestartReasons);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WorkspacesChanged);
        hash.Add(PresenceChanged);
        hash.Add(NotificationsChanged);
        hash.Add(AppearanceChanged);
        hash.Add(LinksChanged);
        hash.Add(SuspensionChanged);
        hash.Add(LoggingChanged);

        foreach (var id in AddedServiceIds.Concat(RemovedServiceIds)
            .Concat(RecreatedServiceIds).Concat(NavigatedServiceIds).Concat(RestartReasons))
        {
            hash.Add(id);
        }

        return hash.ToHashCode();
    }
}
