using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Muster.Core.Config;
using Muster.Core.Hosting;
using Muster.Core.Notifications;
using Muster.Core.Presence;
using Muster.Core.Services;

namespace Muster.App.ViewModels;

/// <summary>
/// The shell's state: the workspace rail, the tab strip of the active workspace, and which
/// session should currently be on screen.
/// </summary>
public sealed partial class MainViewModel(IConfigStore configStore, ILogger<MainViewModel> log)
    : ObservableObject
{
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];

    [ObservableProperty]
    public partial WorkspaceViewModel? ActiveWorkspace { get; set; }

    /// <summary>Every workspace's counts added up. Drives the tray badge. See SPEC section 7.2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalBadgeText))]
    public partial int TotalUnread { get; set; }

    public string TotalBadgeText => TitleUnreadParser.Format(TotalUnread);

    /// <summary>Notification history, newest first, as shown in the panel.</summary>
    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];

    /// <summary>
    /// The live call state, from microphone use alone. Whether it goes on to write presence
    /// anywhere is <c>presence.enabled</c>'s decision, not this property's.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInCall))]
    [NotifyPropertyChangedFor(nameof(CallStatusText))]
    public partial CallState CallStatus { get; set; } = CallState.Idle;

    /// <summary>Which session's microphone started the call, for the indicator's tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CallStatusText))]
    public partial string? CallSourceName { get; set; }

    public bool IsInCall => CallStatus is CallState.InCall or CallState.Clearing;

    public string CallStatusText => CallStatus switch
    {
        CallState.InCall when CallSourceName is { } name => $"In a call · {name}",
        CallState.InCall => "In a call",
        CallState.Clearing => "Call ending…",
        _ => string.Empty,
    };

    // ---- presence sync ------------------------------------------------------------------------

    /// <summary>
    /// The accounts Muster currently has showing Busy, by label. SPEC section 10 asks for a
    /// visible sign whenever presence has been applied: showing Busy in a client tenant without
    /// knowing why is worse than not syncing at all.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresenceApplied))]
    [NotifyPropertyChangedFor(nameof(PresenceStatusText))]
    [NotifyPropertyChangedFor(nameof(PresenceStatusTooltip))]
    public partial IReadOnlyList<string> BusyAccounts { get; set; } = [];

    /// <summary>Whether the user has forced Busy by hand. See SPEC section 8.4.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresenceApplied))]
    [NotifyPropertyChangedFor(nameof(PresenceStatusText))]
    [NotifyPropertyChangedFor(nameof(PresenceStatusTooltip))]
    public partial bool PresenceOverride { get; set; }

    /// <summary>
    /// The indicator shows for a forced Busy even before anything has been written, because the
    /// switch being on with nothing to show for it is itself something the user needs to see.
    /// </summary>
    public bool IsPresenceApplied => BusyAccounts.Count > 0 || PresenceOverride;

    public string PresenceStatusText => (PresenceOverride, BusyAccounts.Count) switch
    {
        (true, 0) => "Busy forced · nothing applied",
        (true, var n) => $"Busy forced · {n} account{(n == 1 ? string.Empty : "s")}",
        (false, var n) => $"Busy set · {n} account{(n == 1 ? string.Empty : "s")}",
    };

    public string PresenceStatusTooltip => BusyAccounts.Count == 0
        ? "Busy is forced on, but no account has it applied. Check presence.enabled, the account "
          + "strategies, and that each one is signed in under Settings, Presence."
        : "Muster is showing you as Busy in:\n" + string.Join("\n", BusyAccounts);

    public bool HasNotifications => Notifications.Count > 0;

    /// <summary>Where muster.json lives, for the status bar and error messages.</summary>
    public string ConfigPath => configStore.Path;

    /// <summary>
    /// Raised whenever the session that should be on screen changes, whether because the
    /// workspace changed or the tab within it did. The view layer turns this into visibility.
    /// </summary>
    public event EventHandler<SessionDescriptor>? ActiveSessionChanged;

    /// <summary>Raised when a tab is closed, so its session can be torn down.</summary>
    public event EventHandler<SessionDescriptor>? TabClosed;

    /// <summary>Every service that should be brought up at launch rather than on first visit.</summary>
    public IEnumerable<SessionDescriptor> KeepAliveSessions => Workspaces
        .SelectMany(workspace => workspace.Services)
        .Where(service => service.KeepAlive)
        .Select(service => service.Descriptor);

    /// <summary>
    /// Loads the config and builds the rail. Throws <see cref="ConfigException"/> if it is invalid.
    /// </summary>
    /// <returns>The config that was loaded, for the caller to start watching and apply.</returns>
    public async Task<MusterConfig> LoadAsync(CancellationToken ct = default)
    {
        var config = await configStore.LoadAsync(ct).ConfigureAwait(true);

        // Startup is a reload from nothing, so it goes down the same path. One rebuild to get
        // wrong rather than two.
        Rebuild(config, ConfigDiff.None);
        return config;
    }

    /// <summary>
    /// Rebuilds the rail and tab strip from a config, keeping everything the file does not
    /// describe: the tabs the user opened themselves, which tab each workspace was on, and each
    /// tab's live title and badge.
    /// </summary>
    /// <param name="diff">
    /// Which sessions are being torn down. Their tabs start blank, because whatever they were
    /// showing is about to stop existing.
    /// </param>
    /// <returns>
    /// Ephemeral tabs whose workspace is gone from the config. They have nowhere left to live, so
    /// the caller has to dispose their sessions.
    /// </returns>
    public IReadOnlyList<SessionDescriptor> Rebuild(MusterConfig config, ConfigDiff diff)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(diff);

        var liveState = Workspaces
            .SelectMany(workspace => workspace.Tabs)
            .ToDictionary(tab => tab.Id, tab => (tab.Title, tab.IsLive), StringComparer.Ordinal);

        var carried = Workspaces.ToDictionary(
            workspace => workspace.Id,
            workspace => workspace.Tabs.OfType<EphemeralTabViewModel>().ToList(),
            StringComparer.Ordinal);

        var wasActiveTab = Workspaces.ToDictionary(
            workspace => workspace.Id,
            workspace => workspace.ActiveTab?.Id,
            StringComparer.Ordinal);

        var wasActiveWorkspace = ActiveWorkspace?.Id;
        var surviving = config.Workspaces.Select(workspace => workspace.Id).ToHashSet(StringComparer.Ordinal);
        var stopped = diff.StoppedServiceIds.ToHashSet(StringComparer.Ordinal);

        var orphaned = carried
            .Where(pair => !surviving.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .Select(tab => tab.Descriptor)
            .ToList();

        foreach (var workspace in Workspaces)
        {
            workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            workspace.PropertyChanged -= OnAnyWorkspacePropertyChanged;
            workspace.Detach();
        }

        Workspaces.Clear();

        foreach (var workspace in config.Workspaces)
        {
            var viewModel = new WorkspaceViewModel(workspace);

            // Every workspace, not only the active one. A keepAlive service in a background
            // workspace is exactly the case badges exist for.
            viewModel.PropertyChanged += OnAnyWorkspacePropertyChanged;

            foreach (var tab in carried.GetValueOrDefault(workspace.Id) ?? [])
            {
                viewModel.Adopt(tab);
            }

            RestoreLiveState(viewModel, liveState, stopped);

            if (wasActiveTab.GetValueOrDefault(workspace.Id) is { } tabId
                && viewModel.Tabs.FirstOrDefault(tab => tab.Id == tabId) is { } previous)
            {
                viewModel.ActiveTab = previous;
            }

            Workspaces.Add(viewModel);
        }

        log.LogInformation(
            "Loaded {Workspaces} workspace(s), {Services} service(s) from {Path}",
            Workspaces.Count,
            Workspaces.Sum(workspace => workspace.Services.Count()),
            configStore.Path);

        // Setting this raises ActiveSessionChanged, which is what brings the visible session back
        // up, so it goes last: everything else has to be in place first.
        ActiveWorkspace = Workspaces.FirstOrDefault(workspace => workspace.Id == wasActiveWorkspace)
            ?? Workspaces.FirstOrDefault();

        return orphaned;
    }

    /// <summary>
    /// Puts each rebuilt tab back where its predecessor was. Without this every reload blanks the
    /// badges until each page next changes its own title, which for a quiet Teams tab is a while.
    /// </summary>
    private static void RestoreLiveState(
        WorkspaceViewModel workspace,
        IReadOnlyDictionary<string, (string? Title, bool IsLive)> liveState,
        IReadOnlySet<string> stopped)
    {
        foreach (var tab in workspace.Tabs)
        {
            // A session that is going away takes its title and count with it.
            if (stopped.Contains(tab.Id) || !liveState.TryGetValue(tab.Id, out var was))
            {
                continue;
            }

            tab.IsLive = was.IsLive;
            tab.Title = was.Title;
        }
    }

    /// <summary>The workspace a session belongs to, or the active one if it has gone away.</summary>
    public WorkspaceViewModel? WorkspaceOf(string workspaceId)
        => Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) ?? ActiveWorkspace;

    /// <summary>Finds any tab by session id, across every workspace.</summary>
    public TabViewModel? FindTab(string sessionId)
        => Workspaces.SelectMany(workspace => workspace.Tabs)
            .FirstOrDefault(tab => tab.Id == sessionId);

    /// <summary>
    /// Opens an ephemeral tab in <paramref name="workspace"/> and makes it active, switching
    /// workspaces if the request came from one that is not on screen.
    /// </summary>
    public EphemeralTabViewModel OpenEphemeral(WorkspaceViewModel workspace, Uri target)
    {
        var tab = workspace.OpenEphemeral(target);
        log.LogInformation("Opened ephemeral tab {Tab} for {Uri} in {Workspace}", tab.Id, target, workspace.Id);
        return tab;
    }

    /// <summary>Opens an ad-hoc URL typed into the address bar, in the active workspace.</summary>
    public EphemeralTabViewModel? OpenAddress(string? input)
    {
        if (ActiveWorkspace is not { } workspace || !UrlNormaliser.TryNormalise(input, out var target))
        {
            return null;
        }

        var tab = OpenEphemeral(workspace, target);
        Activate(workspace, tab);
        return tab;
    }

    /// <summary>Brings a workspace and one of its tabs to the front.</summary>
    public void Activate(WorkspaceViewModel workspace, TabViewModel tab)
    {
        ActiveWorkspace = workspace;
        workspace.ActiveTab = tab;
    }

    /// <summary>Closes an ephemeral tab and signals that its session should be disposed.</summary>
    public void CloseTab(TabViewModel tab)
    {
        if (!tab.CanClose)
        {
            return;
        }

        var workspace = Workspaces.FirstOrDefault(candidate => candidate.Tabs.Contains(tab));
        workspace?.Close(tab);
        log.LogInformation("Closed ephemeral tab {Tab}", tab.Id);
        TabClosed?.Invoke(this, tab.Descriptor);
    }

    /// <summary>Adds a notification to the top of the panel.</summary>
    public void AddNotification(NotificationRecord record)
    {
        var workspaceName = Workspaces.FirstOrDefault(w => w.Id == record.WorkspaceId)?.Name ?? record.WorkspaceId;
        Notifications.Insert(0, new NotificationItemViewModel(record, workspaceName));
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <summary>Rebuilds the panel from the hub, so it matches history exactly.</summary>
    public void ResetNotifications(IEnumerable<NotificationRecord> history)
    {
        Notifications.Clear();

        foreach (var record in history)
        {
            var workspaceName = Workspaces.FirstOrDefault(w => w.Id == record.WorkspaceId)?.Name ?? record.WorkspaceId;
            Notifications.Add(new NotificationItemViewModel(record, workspaceName));
        }

        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <summary>Ages are relative, so refresh them whenever the panel is shown.</summary>
    public void RefreshNotificationAges()
    {
        foreach (var item in Notifications)
        {
            item.RefreshAge();
        }
    }

    /// <summary>Brings the tab a notification came from to the front.</summary>
    public bool ActivateSource(NotificationRecord record)
    {
        var workspace = Workspaces.FirstOrDefault(w => w.Id == record.WorkspaceId);
        var tab = workspace?.Tabs.FirstOrDefault(t => t.Id == record.SessionId);

        if (workspace is null || tab is null)
        {
            return false;
        }

        Activate(workspace, tab);
        return true;
    }

    /// <summary>Applies a live document title to the tab it came from.</summary>
    public void ApplyTitle(string sessionId, string title)
    {
        if (FindTab(sessionId) is { } tab)
        {
            tab.Title = title;
        }
    }

    partial void OnActiveWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnWorkspacePropertyChanged;
        }

        if (newValue is null)
        {
            return;
        }

        newValue.PropertyChanged += OnWorkspacePropertyChanged;
        newValue.ActiveTab ??= newValue.Tabs.FirstOrDefault();
        RaiseActiveSessionChanged(newValue.ActiveTab);
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.ActiveTab)
            && sender is WorkspaceViewModel workspace
            && ReferenceEquals(workspace, ActiveWorkspace))
        {
            RaiseActiveSessionChanged(workspace.ActiveTab);
        }
    }

    private void OnAnyWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceViewModel.UnreadCount))
        {
            return;
        }

        TotalUnread = Workspaces.Sum(workspace => workspace.UnreadCount);

        log.LogDebug(
            "Unread total {Total} ({Breakdown})",
            TotalUnread,
            string.Join(", ", Workspaces.Select(w => $"{w.Id}={w.UnreadCount}")));
    }

    private void RaiseActiveSessionChanged(TabViewModel? tab)
    {
        if (tab is not null)
        {
            ActiveSessionChanged?.Invoke(this, tab.Descriptor);
        }
    }
}
