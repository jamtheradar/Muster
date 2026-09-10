using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Muster.App.Configuration;
using Muster.App.Diagnostics;
using Muster.App.Hosting;
using Muster.App.Logging;
using Muster.App.Notifications;
using Muster.App.Tray;
using Muster.App.ViewModels;
using Muster.Core.Config;
using Muster.Core.Hosting;
using Muster.Core.Notifications;
using Muster.Core.Presence;
using Muster.Core.Services;

namespace Muster.App.Views;

/// <summary>
/// The shell. Owns the session controls; the view model owns the state and never sees a WebView2.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Ctrl+Alt+B forces Busy everywhere, and clears it again. See SPEC section 8.4.</summary>
    private const int ForceBusyHotkeyId = 0xB05;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;

    /// <summary>Without this, holding the chord repeats it dozens of times a second.</summary>
    private const uint ModNoRepeat = 0x4000;

    private const uint VirtualKeyB = 0x42;
    private const int WmHotkey = 0x0312;

    /// <summary>How long quitting will wait for presence to be cleared before going anyway.</summary>
    private static readonly TimeSpan ClearOnQuitTimeout = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _viewModel;
    private readonly SessionManager _sessions;
    private readonly FileLoggerProvider _logs;
    private readonly TrayIcon _tray;
    private readonly NotificationHub _hub;
    private readonly NotificationCoordinator _notifications;
    private readonly PresenceCoordinator _presence;
    private readonly PresenceApplier _applier;
    private readonly SuspensionCoordinator _suspension;
    private readonly ConfigWatcher _watcher;
    private readonly LiveSettings _live;
    private readonly ExternalLinkOpener _externalLinks;
    private readonly AppIcons _icons;
    private readonly RelaunchProperties _relaunch;
    private readonly UpdateService _updates;
    private readonly Func<SettingsWindow> _settingsFactory;
    private readonly ILogger<MainWindow> _log;
    private readonly Dictionary<string, PopupWindow> _popups = new(StringComparer.Ordinal);
    private readonly TaskbarBadge _taskbar;
    private SettingsWindow? _settings;
    private bool _quitting;

    /// <summary>Ctrl+, opens settings, which is where every desktop app puts it.</summary>
    public static readonly RoutedUICommand OpenSettingsCommand =
        new("Settings", nameof(OpenSettingsCommand), typeof(MainWindow));

    public MainWindow(
        MainViewModel viewModel,
        SessionManager sessions,
        FileLoggerProvider logs,
        TrayIcon tray,
        NotificationHub hub,
        NotificationCoordinator notifications,
        PresenceCoordinator presence,
        PresenceApplier applier,
        SuspensionCoordinator suspension,
        ConfigWatcher watcher,
        LiveSettings live,
        ExternalLinkOpener externalLinks,
        AppIcons icons,
        RelaunchProperties relaunch,
        UpdateService updates,
        Func<SettingsWindow> settingsFactory,
        ILogger<MainWindow> log)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _sessions = sessions;
        _logs = logs;
        _tray = tray;
        _hub = hub;
        _notifications = notifications;
        _presence = presence;
        _applier = applier;
        _suspension = suspension;
        _watcher = watcher;
        _live = live;
        _externalLinks = externalLinks;
        _icons = icons;
        _relaunch = relaunch;
        _updates = updates;
        _settingsFactory = settingsFactory;
        _log = log;

        DataContext = viewModel;

        // Without this the window inherits the exe's own icon, which is baked in at compile time
        // and so cannot follow the setting. Every window the shell owns is given one explicitly
        // for that reason.
        ApplyIconSet();

        // Belongs to this window rather than the container: it is the window's taskbar button it
        // draws on, and there is nothing to inject.
        _taskbar = new TaskbarBadge(this);

        _sessions.Attach(SessionHost);
        _sessions.TitleChanged += OnSessionTitleChanged;
        _sessions.NewWindowRequested += OnNewWindowRequested;
        _sessions.CloseRequested += OnSessionCloseRequested;
        _viewModel.ActiveSessionChanged += OnActiveSessionChanged;
        _viewModel.TabClosed += OnTabClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _tray.ShowRequested += OnTrayShowRequested;
        _tray.QuitRequested += OnTrayQuitRequested;
        _tray.SettingsRequested += OnTraySettingsRequested;
        _tray.LogsRequested += OnTrayLogsRequested;
        _tray.ForceBusyRequested += OnTrayForceBusyRequested;

        _sessions.NotificationRaised += OnNotificationRaised;
        _sessions.MediaStateChanged += OnMediaStateChanged;
        _sessions.SessionStarted += OnSessionStarted;
        _sessions.SessionRemoved += OnSessionRemoved;
        _presence.StateChanged += OnPresenceStateChanged;
        _applier.Applied += OnPresenceWritten;
        _applier.AppliedAccountsChanged += OnBusyAccountsChanged;
        _suspension.SuspendRequested += OnSuspendRequested;
        _suspension.ResumeRequested += OnResumeRequested;
        _hub.Received += OnNotificationStored;
        _hub.Cleared += OnNotificationHistoryReset;
        _hub.Trimmed += OnNotificationHistoryReset;
        _notifications.ActivationRequested += OnNotificationActivationRequested;

        // A floating window has no tab, so the coordinator cannot find it the usual way.
        _notifications.ResolveDetachedSession = sessionId =>
            _popups.TryGetValue(sessionId, out var popup) && !popup.IsDuplicateView
                ? popup.Descriptor
                : null;
        _watcher.Reloaded += OnConfigReloaded;
        _watcher.Failed += OnConfigUnusable;
        _icons.Changed += OnIconSetChanged;

        // Group the panel by workspace then service, as SPEC 7.2 asks.
        var view = CollectionViewSource.GetDefaultView(viewModel.Notifications);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NotificationItemViewModel.GroupName)));

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        MusterConfig config;

        try
        {
            config = await _viewModel.LoadAsync().ConfigureAwait(true);
        }
        catch (ConfigException ex)
        {
            _log.LogError(ex, "Config is not usable");
            StatusText.Text = $"Config error. See {_viewModel.ConfigPath}";
            MessageBox.Show(this, ex.Message, "Muster · config error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // The settings that are not workspaces, through the same call a reload makes.
        _live.Apply(config);

        // Only now: the watcher diffs against what the app is running on, so it must not start
        // before there is one.
        _watcher.Start(config);

        if (!_watcher.IsWatching)
        {
            _log.LogWarning(
                "Not watching {Path}, so config changes will need a restart to take effect",
                _viewModel.ConfigPath);
        }

        // Everything marked keepAlive comes up at launch, whether or not it is the visible tab,
        // so notifications and badges are live from the start rather than from first visit.
        await StartKeepAliveSessionsAsync().ConfigureAwait(true);

        // Anything a previous run left showing Busy is cleared now, before the first sync. A
        // crash mid-call is exactly the case that leaves someone Busy in every client tenant with
        // nothing running to undo it. Not awaited: it is network work, and the shell is usable
        // without it. See SPEC section 12.
        _ = RecoverPresenceAsync();

        UpdateStatus();

        // Deliberately last and deliberately not awaited: an unreachable release feed must not
        // delay a shell that is otherwise ready, and the result is only ever read in About.
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// The launch-time update check. Wrapped rather than fire-and-forget bare, because an
    /// unobserved failure here would be indistinguishable from the feature not existing.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            if (_updates.CheckOnLaunch && _updates.IsInstalled)
            {
                _log.LogInformation("{Result}", await _updates.CheckAsync().ConfigureAwait(true));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The launch update check failed");
        }
    }

    // ---- icon set ---------------------------------------------------------------------------

    /// <summary>
    /// Repaints every window the shell owns. Raised from <see cref="LiveSettings"/>, which only
    /// ever runs on the dispatcher, so there is nothing to marshal here.
    /// </summary>
    private void OnIconSetChanged(object? sender, EventArgs e) => ApplyIconSet();

    /// <summary>
    /// The floating windows are included because they carry their own taskbar buttons: leaving
    /// them on the old mark would show two different Musters side by side on the taskbar.
    /// </summary>
    private void ApplyIconSet()
    {
        var icon = _icons.CurrentWindowIcon;

        Icon = icon;

        // What a *new* pin of this window will be built from. Separate from Icon above, which
        // only governs the button while the window is open: pinning reads the relaunch
        // properties instead, and a repin discards whatever the old shortcut said.
        _relaunch.Apply(new WindowInteropHelper(this).Handle, _icons.Current);

        foreach (var popup in _popups.Values)
        {
            popup.Icon = icon;
        }

        if (_settings is { } settings)
        {
            settings.Icon = icon;
        }
    }

    // ---- config hot-reload ------------------------------------------------------------------

    /// <summary>Fires on a thread pool thread; everything it touches belongs to the dispatcher.</summary>
    private void OnConfigReloaded(object? sender, ConfigReloadedEventArgs e)
        => Dispatcher.BeginInvoke(() => _ = ApplyReloadAsync(e));

    /// <summary>
    /// Applies a changed config to the running shell. Sessions the change cannot be applied to go
    /// first, then the rail is rebuilt around what is left, then anything missing comes back up.
    /// </summary>
    private async Task ApplyReloadAsync(ConfigReloadedEventArgs e)
    {
        var diff = e.Diff;

        try
        {
            _log.LogInformation("Config reloaded from disk: {Summary}", Describe(diff));

            _live.Apply(e.Current);

            // Before the rebuild, or the rail would briefly try to show a session on its way out.
            foreach (var sessionId in diff.StoppedServiceIds)
            {
                _sessions.Remove(sessionId);
            }

            // Ephemeral tabs whose workspace is gone from the config have nowhere left to live.
            foreach (var orphan in _viewModel.Rebuild(e.Current, diff))
            {
                _sessions.Remove(orphan.Id);
            }

            NavigateChangedUrls(diff);
            RetrackSuspension();

            // An edit can add an account, remove one that is currently Busy, or switch presence
            // off altogether. All three are the same reconcile.
            _ = SyncPresenceAsync("config reload");

            // Newly added or just recreated keepAlive services.
            await StartKeepAliveSessionsAsync().ConfigureAwait(true);

            UpdateStatus();

            StatusText.Text = diff.RequiresRestart
                ? $"Config reloaded. {diff.RestartReasons[0]}"
                : "Config reloaded.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not apply the reloaded config");
            UpdateStatus();
            StatusText.Text = "Config reloaded but could not be applied in full. See the log.";
        }
    }

    /// <summary>
    /// Hands the suspension coordinator the descriptors the surviving sessions now answer to.
    /// </summary>
    /// <remarks>
    /// A live session carries the descriptor it was started with, and <c>keepAlive</c> is editable
    /// without the session being recreated — deliberately, since recreating would cost a sign-in.
    /// Without this, clearing keepAlive on a running service would leave it unsuspendable until
    /// the next launch, which is the sort of thing that gets diagnosed as suspension not working.
    /// </remarks>
    private void RetrackSuspension()
    {
        foreach (var sessionId in _sessions.LiveSessionIds.ToList())
        {
            if (_viewModel.FindTab(sessionId) is { } tab)
            {
                _suspension.Track(tab.Descriptor);
            }
        }
    }

    /// <summary>
    /// A service whose only change is its URL keeps its session, and therefore its cookie jar and
    /// its sign-in. Tearing it down and letting it come back would cost an auth round trip at
    /// best, and a fresh sign-in at worst.
    /// </summary>
    private void NavigateChangedUrls(ConfigDiff diff)
    {
        foreach (var sessionId in diff.NavigatedServiceIds)
        {
            if (_sessions.Find(sessionId) is not { } session
                || _viewModel.FindTab(sessionId) is not { } tab)
            {
                continue;
            }

            session.Navigate(tab.Descriptor.Home);
            _log.LogInformation("Session {Session} sent to its new home {Uri}", sessionId, tab.Descriptor.Home);
        }
    }

    private async Task StartKeepAliveSessionsAsync()
    {
        foreach (var descriptor in _viewModel.KeepAliveSessions.ToList())
        {
            if (_sessions.Find(descriptor.Id) is null)
            {
                await SafeStartAsync(descriptor, show: false).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// A hand edit in progress is not an error state: the running config is kept and the app
    /// carries on. Saying so in the status bar beats a modal over a file the user is still typing.
    /// </summary>
    private void OnConfigUnusable(object? sender, ConfigException e)
        => Dispatcher.BeginInvoke(() =>
        {
            _log.LogWarning(e, "The config on disk is not usable; keeping the one already running");
            StatusText.Text = $"{_viewModel.ConfigPath} has a problem, so it was not applied. See the log.";
        });

    private static string Describe(ConfigDiff diff)
    {
        var parts = new List<string>();

        Add(parts, "added", diff.AddedServiceIds);
        Add(parts, "removed", diff.RemovedServiceIds);
        Add(parts, "recreated", diff.RecreatedServiceIds);
        Add(parts, "navigated", diff.NavigatedServiceIds);

        if (diff.PresenceChanged)
        {
            parts.Add("presence settings");
        }

        if (diff.NotificationsChanged)
        {
            parts.Add("notification settings");
        }

        if (diff.AppearanceChanged)
        {
            parts.Add("icon set");
        }

        if (diff.LinksChanged)
        {
            parts.Add("link settings");
        }

        if (diff.SuspensionChanged)
        {
            parts.Add("suspension settings");
        }

        if (diff.LoggingChanged)
        {
            parts.Add("logging settings");
        }

        return parts.Count == 0 ? "workspace details only" : string.Join(", ", parts);

        static void Add(List<string> parts, string label, IReadOnlyList<string> ids)
        {
            if (ids.Count > 0)
            {
                parts.Add($"{label} {string.Join("/", ids)}");
            }
        }
    }

    // ---- call detection -------------------------------------------------------------------

    private void OnMediaStateChanged(object? sender, MediaStateChangedEventArgs e)
    {
        if (e.Acquired)
        {
            _presence.ReportAudioAcquired(e.SessionId);
        }
        else
        {
            _presence.ReportAudioReleased(e.SessionId);
        }
    }

    private void OnSessionStarted(object? sender, SessionDescriptor descriptor)
        => _suspension.Track(descriptor);

    private void OnSessionRemoved(object? sender, SessionDescriptor descriptor)
    {
        _presence.ReportSessionGone(descriptor.Id);
        _suspension.Forget(descriptor.Id);
    }

    // ---- suspension ------------------------------------------------------------------------

    /// <summary>
    /// Answers the coordinator's request to put a session to sleep. Deliberately fire-and-forget:
    /// suspension is an optimisation, so nothing waits on it and nothing fails because of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fires on a timer thread</b>, like every other coordinator event, and everything it
    /// touches is a WebView2 control. Marshalling is not tidiness here: this handler used to be
    /// <c>async void</c> called straight off that thread, so it ran as far as
    /// <c>HostedSession.Core</c> — a WPF object — before its first await, and WPF's
    /// <c>VerifyAccess</c> threw. An <c>async void</c> with no synchronization context sends that
    /// exception to the thread pool, where nothing catches it and the process dies. It presented
    /// as Muster vanishing without a word, roughly <c>idleMinutes</c> after launch.
    /// </para>
    /// <para>
    /// The lambda is what makes the marshalling real. Putting <c>async void</c> on the handler and
    /// awaiting inside it would still execute the first synchronous stretch on the timer thread,
    /// which is precisely where the fault was.
    /// </para>
    /// </remarks>
    private void OnSuspendRequested(object? sender, SessionDescriptor descriptor)
        => Dispatcher.BeginInvoke(() => _ = SuspendAsync(descriptor));

    private async Task SuspendAsync(SessionDescriptor descriptor)
    {
        try
        {
            if (await _sessions.TrySuspendAsync(descriptor.Id).ConfigureAwait(true))
            {
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            // Suspension is an optimisation. It must never be the reason the shell goes down.
            _log.LogWarning(ex, "Could not suspend {Session}", descriptor.Id);
        }
    }

    /// <summary>Also fires on a timer thread. See the note on <see cref="OnSuspendRequested"/>.</summary>
    private void OnResumeRequested(object? sender, SessionDescriptor descriptor)
        => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _sessions.Resume(descriptor.Id);
                UpdateStatus();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not resume {Session}", descriptor.Id);
            }
        });

    /// <summary>
    /// The call state changed. The indicator is updated here; whether anything is written to
    /// Graph is <see cref="PresenceApplier"/>'s decision and <c>presence.enabled</c>'s.
    /// </summary>
    private void OnPresenceStateChanged(object? sender, PresenceStateChangedEventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            _viewModel.CallStatus = e.Current;
            _viewModel.CallSourceName = e.SourceSessionId is { } id
                ? _viewModel.FindTab(id)?.DisplayName ?? id
                : null;

            CallIndicator.Visibility = _viewModel.IsInCall ? Visibility.Visible : Visibility.Collapsed;

            // A session holding audio is never a suspension candidate, whatever its idle time says.
            _suspension.SetCallSessions(e.CallSessions);

            _log.LogInformation(
                "Call state {Previous} to {Current}, source {Source}, in call: {Sessions}",
                e.Previous,
                e.Current,
                e.SourceSessionId ?? "none",
                e.CallSessions.Count == 0 ? "none" : string.Join(", ", e.CallSessions));

            // Not awaited: a Graph round trip must never hold up the indicator, and the applier
            // serialises its own work, so a fast Idle-InCall-Idle cannot get out of order.
            _ = SyncPresenceAsync($"call state {e.Current}", e);
        });

    // ---- presence sync ----------------------------------------------------------------------

    private async Task RecoverPresenceAsync()
    {
        try
        {
            var outcome = await _applier.RecoverAsync().ConfigureAwait(true);

            if (outcome.DidAnything)
            {
                _log.LogWarning(
                    "Cleared presence left behind by a previous run: {Outcome}. Something ended " +
                    "mid-call.",
                    outcome);

                StatusText.Text = "Cleared a Busy left behind by the previous run.";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not clear presence left over from a previous run");
        }
    }

    /// <summary>
    /// Brings every account's presence into line with the call state, the config and the manual
    /// override. Safe to call when nothing has changed: the applier writes only differences.
    /// </summary>
    private async Task SyncPresenceAsync(string because, PresenceStateChangedEventArgs? change = null)
    {
        try
        {
            var outcome = change is null
                ? await _applier.SyncAsync().ConfigureAwait(true)
                : await _applier.SyncAsync(change).ConfigureAwait(true);

            if (outcome.DidAnything)
            {
                _log.LogInformation("Presence sync after {Because}: {Outcome}", because, outcome);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Presence sync after {Because} failed", because);
        }
    }

    /// <summary>One write attempt against one account, successful or not. Core does not log.</summary>
    private void OnPresenceWritten(object? sender, PresenceActionEventArgs e)
    {
        if (e.Succeeded)
        {
            _log.LogInformation("Presence {Action} for {Account}", e.Action, e.Account.Label);
            return;
        }

        _log.LogWarning(
            "Presence {Action} for {Account} failed. It is still showing whatever it was.",
            e.Action,
            e.Account.Label);
    }

    /// <summary>
    /// Keeps the indicator honest. SPEC section 10 wants a visible sign whenever presence has been
    /// applied: showing Busy in a client tenant with no idea why is worse than not syncing at all.
    /// </summary>
    private void OnBusyAccountsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(RefreshPresenceIndicator);

    private void RefreshPresenceIndicator()
    {
        _viewModel.BusyAccounts = _applier.AppliedAccounts.Select(account => account.Label).ToList();
        _viewModel.PresenceOverride = _applier.ManualOverride;

        PresenceIndicator.Visibility = _viewModel.IsPresenceApplied
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---- manual override --------------------------------------------------------------------

    private void OnTrayForceBusyRequested(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => _ = ToggleForceBusyAsync());

    /// <summary>
    /// Forces Busy on every account, or lets the call state decide again. For a call in something
    /// this app cannot see — a phone — and as the escape hatch if automatic clearing misbehaves.
    /// </summary>
    private async Task ToggleForceBusyAsync()
    {
        var wanted = !_applier.ManualOverride;

        try
        {
            var outcome = await _applier.SetManualOverrideAsync(wanted).ConfigureAwait(true);

            _log.LogInformation(
                "Manual presence override turned {State}: {Outcome}",
                wanted ? "on" : "off",
                outcome);

            StatusText.Text = wanted
                ? $"Busy forced on every account. {outcome}."
                : $"Forced Busy released. {outcome}.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not turn the manual presence override {State}", wanted ? "on" : "off");
            StatusText.Text = "The manual presence override could not be applied. See the log.";
        }
        finally
        {
            _tray.SetForceBusy(_applier.ManualOverride);
            RefreshPresenceIndicator();
        }
    }

    // ---- notifications --------------------------------------------------------------------

    private void OnNotificationRaised(object? sender, RaisedNotification raised)
        => _notifications.Handle(raised);

    private void OnNotificationStored(object? sender, NotificationRecord record)
    {
        _viewModel.AddNotification(record);
        UpdateNotificationPanelState();
    }

    /// <summary>
    /// The history changed wholesale: emptied, or trimmed by a lowered limit. Either way the
    /// panel is rebuilt from the hub rather than patched.
    /// </summary>
    private void OnNotificationHistoryReset(object? sender, EventArgs e)
    {
        _viewModel.ResetNotifications(_hub.History);
        UpdateNotificationPanelState();
    }

    /// <summary>A toast or a panel row was clicked: go to where it came from.</summary>
    private void OnNotificationActivationRequested(object? sender, NotificationRecord record)
        => Dispatcher.BeginInvoke(() =>
        {
            // A notification from a floating window belongs to that window, not to the shell.
            // Raising the shell and hunting for a tab that was never created would take the user
            // somewhere unrelated to what they clicked.
            if (_popups.TryGetValue(record.SessionId, out var popup))
            {
                popup.Activate();
                return;
            }

            BringToFront();

            if (!_viewModel.ActivateSource(record))
            {
                _log.LogInformation("Notification {Notification} has no tab to open any more", record.Id);
            }
        });

    private void OnBellClick(object sender, RoutedEventArgs e)
    {
        var opening = NotificationPanel.Visibility != Visibility.Visible;
        NotificationPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;

        if (opening)
        {
            // Ages are relative, so they are only correct at the moment the panel opens.
            _viewModel.RefreshNotificationAges();
        }
    }

    private void OnClearNotificationsClick(object sender, RoutedEventArgs e) => _hub.Clear();

    private void OnNotificationActivated(object sender, MouseButtonEventArgs e)
    {
        if (NotificationList.SelectedItem is NotificationItemViewModel item)
        {
            _notifications.Activate(item.Record);
        }
    }

    private void OnNotificationListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && NotificationList.SelectedItem is NotificationItemViewModel item)
        {
            e.Handled = true;
            _notifications.Activate(item.Record);
        }
    }

    private void UpdateNotificationPanelState()
        => NotificationEmptyText.Visibility = _viewModel.HasNotifications
            ? Visibility.Collapsed
            : Visibility.Visible;

    // ---- tray and window state ----------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TotalUnread))
        {
            // Both places Windows will take a badge. The tray one is what you see when the window
            // is hidden; this one is what you see when it is open behind something else.
            _tray.Update(_viewModel.TotalUnread);
            _taskbar.Update(_viewModel.TotalUnread);
        }
        else if (e.PropertyName == nameof(MainViewModel.ActiveWorkspace))
        {
            // Wakes the workspace being shown and starts the clock on the one being left.
            _suspension.SetActiveWorkspace(_viewModel.ActiveWorkspace?.Id);
        }
    }

    private void OnTrayShowRequested(object? sender, EventArgs e) => BringToFront();

    // ---- settings and logs ----------------------------------------------------------------

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    private void OnSettingsCommand(object sender, ExecutedRoutedEventArgs e) => ShowSettings();

    private void OnTraySettingsRequested(object? sender, EventArgs e)
    {
        // The tray menu works while the window is hidden, and a settings window with nothing
        // behind it is disorienting. Bring the shell back first.
        BringToFront();
        ShowSettings();
    }

    private void OnLogsClick(object sender, RoutedEventArgs e) => OpenLogFolder();

    private void OnTrayLogsRequested(object? sender, EventArgs e) => OpenLogFolder();

    private void OpenLogFolder()
    {
        if (!FileExplorer.OpenFolder(_logs.Directory, _log))
        {
            StatusText.Text = $"Could not open {_logs.Directory}";
        }
    }

    /// <summary>
    /// One settings window at a time, reused while it is open. Two of them editing the same file
    /// would mean whichever saved last silently won.
    /// </summary>
    private void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = _settingsFactory();
            _settings.Owner = this;
            _settings.Icon = _icons.CurrentWindowIcon;
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }

        if (_settings.WindowState == WindowState.Minimized)
        {
            _settings.WindowState = WindowState.Normal;
        }

        _settings.Activate();
    }

    /// <summary>
    /// Un-hides the window, whether it went to the tray or is merely behind something. Also the
    /// entry point a second launch uses to hand control back to this instance.
    /// </summary>
    public void BringToFront()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    private async void OnTrayQuitRequested(object? sender, EventArgs e)
    {
        _quitting = true;

        // Take the settings window down without its discard prompt. Quit means quit, and a modal
        // question raised as the app is shutting down has nothing left to go back to.
        _settings?.ForceClose();

        // Quitting while Busy would leave it in place until expirationDuration ran out, which is
        // two hours of looking unavailable in every client tenant. Capped, because a Graph outage
        // must not be able to stop the app closing — the journal and the expiry both cover the
        // case where this does not finish.
        await ClearPresenceForQuitAsync().ConfigureAwait(true);

        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task ClearPresenceForQuitAsync()
    {
        if (_applier.AppliedAccounts.Count == 0)
        {
            return;
        }

        StatusText.Text = "Clearing presence...";

        try
        {
            using var timeout = new CancellationTokenSource(ClearOnQuitTimeout);
            var outcome = await _applier.ClearAllAsync(timeout.Token).ConfigureAwait(true);
            _log.LogInformation("Cleared presence on the way out: {Outcome}", outcome);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Presence could not be cleared within {Seconds}s of quitting. The journal will " +
                "clear it at next launch, and the expiry will clear it regardless.",
                ClearOnQuitTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Presence could not be cleared on the way out");
        }
    }

    // ---- global hotkey ----------------------------------------------------------------------

    /// <summary>
    /// Registers Ctrl+Alt+B for the manual override, and claims the window's pin identity. Global
    /// hotkey on purpose: the case it exists for is a call in something that is not this app, so
    /// reaching for Muster's window first defeats it.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).EnsureHandle();

        // The first moment there is a handle to hang them on. ApplyIconSet ran in the constructor,
        // before the window had one, so its call was a no-op.
        _relaunch.Apply(handle, _icons.Current);

        if (HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook(OnWindowMessage);
        }
        else
        {
            _log.LogWarning("No HWND source for the shell window, so Ctrl+Alt+B cannot be delivered");
        }

        if (RegisterHotKey(handle, ForceBusyHotkeyId, ModControl | ModAlt | ModNoRepeat, VirtualKeyB))
        {
            _log.LogInformation("Ctrl+Alt+B registered for the manual presence override");
        }
        else
        {
            // Another app already owns the chord. Not fatal, and not worth a dialog: the tray menu
            // is the same command.
            _log.LogWarning(
                "Ctrl+Alt+B is already taken, so the presence override is tray-menu only " +
                "(error {Error})",
                Marshal.GetLastWin32Error());
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != ForceBusyHotkeyId)
        {
            return IntPtr.Zero;
        }

        handled = true;
        _ = ToggleForceBusyAsync();
        return IntPtr.Zero;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnClosing(CancelEventArgs e)
    {
        // Close to tray. Quitting for real is the tray menu's Quit, so a stray Alt+F4 never
        // silently takes every session down with it.
        //
        // Minimise deliberately does not come here. Hiding on minimise took the taskbar button
        // away with it, so the ordinary "put it out of the way for a minute" gesture looked
        // exactly like the app quitting, and the taskbar badge went with it. Minimise is now a
        // plain minimise; the tray is reached by closing.
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
            _tray.AnnounceHiddenOnce();
            return;
        }

        base.OnClosing(e);
    }

    // ---- new window routing -------------------------------------------------------------

    /// <summary>
    /// Decides where a page's new window goes: a pinned tab, a floating window, a throwaway tab,
    /// or - only where the registered handler routes by profile - out to Windows.
    /// </summary>
    private async void OnNewWindowRequested(object? sender, NewWindowRequest request)
    {
        try
        {
            var workspace = _viewModel.WorkspaceOf(request.Source.Descriptor.WorkspaceId);
            if (workspace is null)
            {
                _log.LogWarning("Dropping new window for {Uri}: no workspace", request.Target);
                return;
            }

            // Asked once per click rather than held: the registered handler can change under
            // Settings while the app is running, and a stale answer is invisible until a link
            // opens somewhere it should not have.
            var allowExternal = _externalLinks.IsEnabled;

            var route = NewWindowRouter.Route(
                workspace.PinnedDescriptors,
                request.Target,
                request.IsScriptedPopup,
                allowExternal);

            // Every routing input, in one line. Whether a page asked for a window or a tab is not
            // something you can tell by watching the result, and the two look identical in a log
            // that only records what was opened.
            // Safe Links makes every link in a Teams message present the same host, so a line
            // recording only what the page handed us cannot tell two decisions apart.
            var destination = SafeLinks.TryUnwrap(request.Target, out var unwrapped)
                ? unwrapped.ToString()
                : "not wrapped";

            _log.LogDebug(
                "New window for {Uri}: scripted {Scripted}, user initiated {User}, " +
                "requested {Width}x{Height} at {Left},{Top}, destination {Destination}, " +
                "external allowed {External} -> {Route}",
                request.Target,
                request.IsScriptedPopup,
                request.IsUserInitiated,
                request.RequestedWidth,
                request.RequestedHeight,
                request.RequestedLeft,
                request.RequestedTop,
                destination,
                allowExternal,
                route.GetType().Name);

            switch (route)
            {
                case NewWindowRoute.ActivatePinnedService(var serviceId)
                    when workspace.Tabs.FirstOrDefault(tab => tab.Id == serviceId) is { } pinned:
                {
                    // The URL belongs to a tab that already exists, so reuse it rather than
                    // accumulating duplicates of the same service.
                    _viewModel.Activate(workspace, pinned);
                    var session = await _sessions.ShowAsync(pinned.Descriptor).ConfigureAwait(true);
                    session.Navigate(request.Target);
                    _log.LogInformation("Routed {Uri} into pinned tab {Tab}", request.Target, serviceId);
                    break;
                }

                case NewWindowRoute.OpenFloatingWindow:
                {
                    await OpenFloatingWindowAsync(workspace, request).ConfigureAwait(true);
                    break;
                }

                case NewWindowRoute.OpenExternally when _externalLinks.TryOpen(request.Target):
                {
                    // Deliberately not adopted, so the page's window.open returns null. Only a
                    // plain target=_blank link is ever routed here and none of them read it;
                    // anything needing a live window.opener is a scripted popup, which the router
                    // sends to a window of ours instead.
                    StatusText.Text = $"Opened {request.Target.Host} outside Muster.";
                    break;
                }

                default:
                {
                    // Also where a failed external open lands. Nothing has been completed yet, so
                    // the link is still ours to place, and a tab beats losing the click.
                    await OpenEphemeralTabAsync(workspace, request).ConfigureAwait(true);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to route new window for {Uri}", request.Target);

            // Without this the link simply vanishes: the deferral completes, the page believes it
            // opened a window, and clicking a document in Teams appears to do nothing at all.
            // Opening it as a plain tab loses window.opener, which a SharePoint document does not
            // need — and the alternative is the system browser, in the wrong identity, which is
            // the one outcome this app exists to prevent.
            await FallBackToPlainTabAsync(request).ConfigureAwait(true);
        }
        finally
        {
            request.Complete();
            UpdateStatus();
        }
    }

    /// <summary>
    /// A throwaway tab in the workspace that asked, adopted so the page keeps a live handle on it.
    /// </summary>
    private async Task OpenEphemeralTabAsync(WorkspaceViewModel workspace, NewWindowRequest request)
    {
        var tab = _viewModel.OpenEphemeral(workspace, request.Target);

        // Adopt before completing: the page needs a session that has not navigated, which is what
        // keeps window.opener and window.close() working.
        var session = await _sessions.CreateForAdoptionAsync(tab.Descriptor).ConfigureAwait(true);

        if (session.Core is { } core)
        {
            request.Adopt(core);
        }

        tab.IsLive = true;
        _viewModel.Activate(workspace, tab);

        _log.LogInformation(
            "Opened {Uri} as an ephemeral tab in workspace {Workspace}",
            request.Target,
            workspace.Id);
    }

    /// <summary>Last resort for a new window that could not be adopted. Never the system browser.</summary>
    private async Task FallBackToPlainTabAsync(NewWindowRequest request)
    {
        try
        {
            if (_viewModel.WorkspaceOf(request.Source.Descriptor.WorkspaceId) is not { } workspace)
            {
                return;
            }

            var tab = _viewModel.OpenEphemeral(workspace, request.Target);
            _viewModel.Activate(workspace, tab);

            await SafeStartAsync(tab.Descriptor, show: true).ConfigureAwait(true);

            StatusText.Text = $"Opened {request.Target.Host} in a tab; it could not be adopted as a window.";
            _log.LogInformation("Recovered {Uri} as a plain tab", request.Target);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not even fall back to a tab for {Uri}", request.Target);
        }
    }

    /// <summary>
    /// Opens a page's sized popup as a real top-level window, so a popped-out meeting can be
    /// dragged to a second monitor instead of being trapped in the tab strip. See SPEC 6.2.
    /// </summary>
    /// <remarks>
    /// The order here is the one WebView2 insists on and is easy to get wrong: show the window
    /// first so it has a realised handle, then create the session into it, then hand the control
    /// over as <c>e.NewWindow</c>. Creating the session before the window is on screen leaves the
    /// control with no handle; navigating it first makes WebView2 reject it outright.
    /// </remarks>
    private async Task OpenFloatingWindowAsync(WorkspaceViewModel workspace, NewWindowRequest request)
    {
        var (window, descriptor) = NewFloatingWindow(workspace, request.Target);
        window.ApplyRequestedBounds(
            request.RequestedLeft,
            request.RequestedTop,
            request.RequestedWidth,
            request.RequestedHeight);

        window.Show();

        HostedSession session;

        try
        {
            session = await _sessions
                .CreateForAdoptionAsync(descriptor, window.Host)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            // No session means nothing to put in the window, and an empty one would just confuse.
            window.Close();
            throw;
        }

        if (session.Core is { } core)
        {
            request.Adopt(core);
        }

        Attach(window, descriptor);

        _log.LogInformation(
            "Opened {Uri} as a floating window for workspace {Workspace}",
            request.Target,
            workspace.Id);
    }

    /// <summary>
    /// Builds the window and the descriptor for a floating session, without showing either. The
    /// window is deliberately unowned: an owned one is pinned above its owner in the z-order,
    /// which is wrong for something popped out to sit beside the shell. The cost is that WPF will
    /// not close it for us, which is what the loop in OnClosed is for.
    /// </summary>
    private (PopupWindow Window, SessionDescriptor Descriptor) NewFloatingWindow(
        WorkspaceViewModel workspace,
        Uri target)
    {
        var descriptor = SessionDescriptor.ForPopup(
            workspace.Id,
            workspace.ProfileName,
            target,
            workspace.PermissionOrigins);

        var window = new PopupWindow { Icon = _icons.CurrentWindowIcon };
        window.ApplyAccent(workspace.AccentBrush);
        return (window, descriptor);
    }

    private void Attach(PopupWindow window, SessionDescriptor descriptor)
    {
        window.Adopt(descriptor);
        window.SessionClosed += OnPopupClosed;
        _popups[descriptor.Id] = window;
    }

    // ---- pop out ----------------------------------------------------------------------------

    private async void OnPopOutClick(object sender, RoutedEventArgs e)
        => await PopOutActiveTabAsync().ConfigureAwait(true);

    /// <summary>
    /// Opens the active tab's current page in a window of its own. See SPEC 6.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This opens the URL again in a new session; it does not move the running one. A WebView2
    /// control cannot be reparented between windows without its controller being torn down and
    /// rebuilt, which would cost the sign-in and drop anything the page was in the middle of — the
    /// exact thing the rest of this shell is built to avoid. So the popped-out window starts fresh
    /// at the same address.
    /// </para>
    /// <para>
    /// The consequence worth knowing: a call or meeting already running in the tab does not travel
    /// with it. The new window is a second, signed-in-but-idle view of the same service.
    /// </para>
    /// <para>
    /// An ephemeral tab genuinely moves — it is transient, so it closes behind you. A pinned
    /// service cannot: its session is the service, and tearing it down is what
    /// <see cref="ConfigDiff"/> exists to avoid. Popping one out therefore leaves the tab running
    /// and marks the window a duplicate view, so the service's notifications keep arriving once
    /// rather than twice.
    /// </para>
    /// </remarks>
    private async Task PopOutActiveTabAsync()
    {
        if (_viewModel.ActiveWorkspace is not { } workspace
            || workspace.ActiveTab is not { } tab
            || _sessions.Find(tab.Id) is not { } session)
        {
            StatusText.Text = "Nothing to pop out: this tab is not running yet.";
            return;
        }

        // Where the page actually is, not where the tab was configured to start.
        var target = session.CurrentUri ?? tab.Descriptor.Home;
        var moving = tab.CanClose;

        var (window, descriptor) = NewFloatingWindow(workspace, target);
        window.IsDuplicateView = !moving;
        window.Title = $"{tab.DisplayName} — Muster";
        window.Show();

        try
        {
            await _sessions.CreateDetachedAsync(descriptor, window.Host).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            window.Close();
            _log.LogError(ex, "Could not pop out {Tab}", tab.Id);
            StatusText.Text = "Could not pop out this tab. See the log.";
            return;
        }

        Attach(window, descriptor);

        if (moving)
        {
            // A transient tab has nothing worth keeping behind, so this is a move rather than a
            // copy — leaving it would be two identical tabs for one thing the user popped out.
            _viewModel.CloseTab(tab);
        }

        _log.LogInformation(
            "Popped {Tab} out to a window as {Session} ({Mode})",
            tab.Id,
            descriptor.Id,
            moving ? "moved" : "second view, tab still running");

        StatusText.Text = moving
            ? $"Popped {tab.DisplayName} out into its own window."
            : $"Opened {tab.DisplayName} in a second window. The tab is still running.";

        UpdateStatus();
    }

    /// <summary>
    /// The window has gone, by the page's <c>window.close()</c> or by the user. Either way the
    /// session behind it has to be torn down: the control is parented to a dead window, and a
    /// renderer left running would hold a microphone the call detector still believes in.
    /// </summary>
    private void OnPopupClosed(object? sender, SessionDescriptor descriptor)
    {
        if (sender is PopupWindow window)
        {
            window.SessionClosed -= OnPopupClosed;
        }

        _popups.Remove(descriptor.Id);
        _sessions.Remove(descriptor.Id);
        UpdateStatus();
    }

    // ---- ad-hoc address bar -------------------------------------------------------------

    private void OnNewTabClick(object sender, RoutedEventArgs e) => ToggleAddressBar();

    private void ToggleAddressBar()
    {
        var opening = AddressBar.Visibility != Visibility.Visible;
        AddressBar.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;

        if (opening)
        {
            AddressInput.Clear();
            AddressInput.Focus();
        }
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                AddressBar.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;

            case Key.Enter:
                e.Handled = true;
                if (_viewModel.OpenAddress(AddressInput.Text) is null)
                {
                    StatusText.Text = $"'{AddressInput.Text}' is not a URL Muster can open.";
                    return;
                }

                AddressBar.Visibility = Visibility.Collapsed;
                break;

            default:
                break;
        }
    }

    // ---- tab lifecycle ------------------------------------------------------------------

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            _viewModel.CloseTab(tab);
        }
    }

    /// <summary>A page called <c>window.close()</c> on itself.</summary>
    private void OnSessionCloseRequested(object? sender, SessionDescriptor descriptor)
    {
        // Teams ends a popped-out meeting with window.close(), so this has to shut the window
        // rather than look for a tab that was never created.
        if (_popups.TryGetValue(descriptor.Id, out var popup))
        {
            popup.CloseFromPage();
            return;
        }

        if (_viewModel.FindTab(descriptor.Id) is { CanClose: true } tab)
        {
            _viewModel.CloseTab(tab);
            return;
        }

        _log.LogInformation("Ignoring close request from pinned session {Session}", descriptor.Id);
    }

    private void OnTabClosed(object? sender, SessionDescriptor descriptor)
    {
        _sessions.Remove(descriptor.Id);
        UpdateStatus();
    }

    // ---- session activation -------------------------------------------------------------

    private async void OnActiveSessionChanged(object? sender, SessionDescriptor descriptor)
        => await SafeStartAsync(descriptor, show: true).ConfigureAwait(true);

    private async Task SafeStartAsync(SessionDescriptor descriptor, bool show)
    {
        try
        {
            if (show)
            {
                await _sessions.ShowAsync(descriptor).ConfigureAwait(true);
            }
            else
            {
                await _sessions.PreloadAsync(descriptor).ConfigureAwait(true);
            }

            if (_viewModel.FindTab(descriptor.Id) is { } tab)
            {
                tab.IsLive = true;
            }

            UpdateStatus();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start session {Session}", descriptor.Id);
            StatusText.Text = $"{descriptor.Name} failed to start: {ex.Message}";
        }
    }

    private void UpdateStatus()
    {
        var active = _sessions.Active?.Descriptor;

        // The asleep count is the only visible sign suspension is working at all, and the first
        // thing to look at when a background service seems to have gone quiet.
        var asleep = _sessions.SuspendedCount;
        var live = asleep > 0 ? $"{_sessions.LiveCount} live, {asleep} asleep" : $"{_sessions.LiveCount} live";

        RuntimeText.Text = _sessions.Active?.Core is { } core
            ? $"WebView2 {core.Environment.BrowserVersionString}  ·  {live}"
            : live;

        StatusText.Text = active is null
            ? $"config: {_viewModel.ConfigPath}"
            : $"profile {active.ProfileName}  ·  config: {_viewModel.ConfigPath}  ·  logs: {_logs.Directory}";

        SetWindowTitle(_sessions.Active?.DocumentTitle);
    }

    // The title event only fires when a page changes its own title, so switching tabs has to
    // pull the new tab's current one or the window keeps advertising the tab you just left.
    private void SetWindowTitle(string? title)
        => Title = string.IsNullOrWhiteSpace(title) ? "Muster" : $"{title} — Muster";

    private void OnSessionTitleChanged(object? sender, SessionTitleChangedEventArgs e)
    {
        // A floating session has no tab and no place on the rail; its title belongs to its window.
        if (_popups.TryGetValue(e.SessionId, out var popup))
        {
            popup.SetTitle(e.Title);
            return;
        }

        _viewModel.ApplyTitle(e.SessionId, e.Title);

        if (_sessions.Active?.Descriptor.Id == e.SessionId)
        {
            SetWindowTitle(e.Title);
        }
    }

    private void OnHomeClick(object sender, RoutedEventArgs e) => _sessions.Active?.GoHome();

    private void OnReloadClick(object sender, RoutedEventArgs e) => _sessions.Active?.Reload();

    protected override void OnClosed(EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        if (handle != IntPtr.Zero)
        {
            UnregisterHotKey(handle, ForceBusyHotkeyId);
        }

        // Floating windows are unowned, so nothing else will close them: without this the shell
        // exits and leaves orphaned meeting windows behind with no way back to the app. The
        // handler each one raises is also what tears its session down.
        foreach (var popup in _popups.Values.ToList())
        {
            popup.Close();
        }

        _sessions.TitleChanged -= OnSessionTitleChanged;
        _sessions.NewWindowRequested -= OnNewWindowRequested;
        _sessions.CloseRequested -= OnSessionCloseRequested;
        _viewModel.ActiveSessionChanged -= OnActiveSessionChanged;
        _viewModel.TabClosed -= OnTabClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _tray.ShowRequested -= OnTrayShowRequested;
        _tray.QuitRequested -= OnTrayQuitRequested;
        _tray.SettingsRequested -= OnTraySettingsRequested;
        _tray.LogsRequested -= OnTrayLogsRequested;
        _tray.ForceBusyRequested -= OnTrayForceBusyRequested;
        _sessions.NotificationRaised -= OnNotificationRaised;
        _sessions.MediaStateChanged -= OnMediaStateChanged;
        _sessions.SessionStarted -= OnSessionStarted;
        _sessions.SessionRemoved -= OnSessionRemoved;
        _presence.StateChanged -= OnPresenceStateChanged;
        _applier.Applied -= OnPresenceWritten;
        _applier.AppliedAccountsChanged -= OnBusyAccountsChanged;
        _suspension.SuspendRequested -= OnSuspendRequested;
        _suspension.ResumeRequested -= OnResumeRequested;
        _hub.Received -= OnNotificationStored;
        _hub.Cleared -= OnNotificationHistoryReset;
        _hub.Trimmed -= OnNotificationHistoryReset;
        _watcher.Reloaded -= OnConfigReloaded;
        _watcher.Failed -= OnConfigUnusable;
        _icons.Changed -= OnIconSetChanged;
        _notifications.ActivationRequested -= OnNotificationActivationRequested;
        _sessions.Dispose();
        base.OnClosed(e);
    }
}
