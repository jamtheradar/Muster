using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Xml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Muster.App.Configuration;
using Muster.App.Diagnostics;
using Muster.App.Hosting;
using Muster.App.Logging;
using Muster.Core.Config;
using Muster.Core.Diagnostics;
using Muster.Core.Presence;
using Muster.Graph;

namespace Muster.App.ViewModels;

/// <summary>
/// The settings screen: every option in muster.json, edited in place. The file stays the source of
/// record and stays hand-editable — this writes exactly the same shape, through the same store, so
/// the golden-file contract holds whichever way the config was produced.
/// </summary>
/// <remarks>
/// This screen only writes. Applying is the config watcher's job, so a save and a hand edit go
/// down exactly one path rather than two that have to be kept in step — which means the only
/// thing this class has to work out is whether the change is one of the few that still needs a
/// relaunch, and <see cref="ConfigDiff"/> answers that.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigStore _store;
    private readonly AppIcons _icons;
    private readonly PinnedShortcut _pinned;
    private readonly UpdateService _updates;
    private readonly FileLoggerProvider _logs;
    private readonly IGraphTokenProvider _tokens;
    private readonly ILogger<SettingsViewModel> _log;
    private MusterConfig? _saved;

    public SettingsViewModel(
        IConfigStore store,
        AppIcons icons,
        PinnedShortcut pinned,
        UpdateService updates,
        FileLoggerProvider logs,
        IGraphTokenProvider tokens,
        ILogger<SettingsViewModel> log)
    {
        _store = store;
        _icons = icons;
        _pinned = pinned;
        _updates = updates;
        UpdateStatus = updates.IsInstalled
            ? "Muster checks for updates when it starts, and applies them the next time you open it."
            : "Running from a build folder, so there is nothing to update.";
        _logs = logs;
        _tokens = tokens;
        _log = log;

        ClientId = string.Empty;

        ConfigPath = store.Path;
        ExpirationDuration = "PT2H";
        CallDetectDelay = "3";
        CallClearDelay = "10";
        HistoryLimit = NotificationConfig.DefaultHistoryLimit.ToString(CultureInfo.InvariantCulture);
        IdleMinutes = SuspensionConfig.DefaultIdleMinutes.ToString(CultureInfo.InvariantCulture);
        RetentionDays = LoggingConfig.DefaultRetentionDays.ToString(CultureInfo.InvariantCulture);
        LogDirectory = string.Empty;
    }

    public static IReadOnlyList<LogVerbosity> Levels { get; } = Enum.GetValues<LogVerbosity>();

    public static IReadOnlyList<ExternalLinkPolicy> LinkPolicies { get; } =
        Enum.GetValues<ExternalLinkPolicy>();

    // ---- workspaces and services ----------------------------------------------------------

    public ObservableCollection<WorkspaceEditorViewModel> Workspaces { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedWorkspace))]
    public partial WorkspaceEditorViewModel? SelectedWorkspace { get; set; }

    public bool HasSelectedWorkspace => SelectedWorkspace is not null;

    // ---- presence ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool PresenceEnabled { get; set; }

    [ObservableProperty]
    public partial string CallDetectDelay { get; set; }

    [ObservableProperty]
    public partial string CallClearDelay { get; set; }

    [ObservableProperty]
    public partial string ExpirationDuration { get; set; }

    /// <summary>
    /// The app registration presence signs in with. Not a secret: a public-client id appears in
    /// every sign-in URL, which is why it belongs in the config rather than the token cache.
    /// </summary>
    [ObservableProperty]
    public partial string ClientId { get; set; }

    /// <summary>
    /// The Teams accounts presence can be written for, as the file on disk describes them. Built
    /// from the saved config rather than the pending edits, because an account has to be saved
    /// before it can be signed in to.
    /// </summary>
    public ObservableCollection<PresenceAccountViewModel> PresenceAccounts { get; } = [];

    public bool HasPresenceAccounts => PresenceAccounts.Count > 0;

    public bool HasNoPresenceAccounts => PresenceAccounts.Count == 0;

    // ---- notifications ----------------------------------------------------------------------

    [ObservableProperty]
    public partial bool ToastsEnabled { get; set; }

    [ObservableProperty]
    public partial string HistoryLimit { get; set; }

    // ---- appearance -------------------------------------------------------------------------

    /// <summary>
    /// Every icon set, with a picture of each. The pictures come from the same files the tray and
    /// the windows draw from, so what is previewed is what is applied.
    /// </summary>
    public IReadOnlyList<IconSetOption> IconSets => _icons.Options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconSetDescription))]
    public partial IconSet IconSet { get; set; }

    public string IconSetDescription =>
        IconSets.FirstOrDefault(option => option.Value == IconSet)?.Description ?? string.Empty;

    /// <summary>
    /// What the pinned taskbar shortcut draws from right now, or the reason there is nothing to
    /// report. Read rather than assumed, for the same reason the link handler is: the shortcut
    /// belongs to Windows, and the only honest thing to show is what it actually says.
    /// </summary>
    [ObservableProperty]
    public partial string PinnedShortcutStatus { get; set; } = string.Empty;

    /// <summary>
    /// Points the pinned taskbar button at the selected set. Separate from Save because it writes
    /// outside muster.json, into the user's own taskbar folder.
    /// </summary>
    [RelayCommand]
    private void UpdatePinnedShortcut() => PinnedShortcutStatus = _pinned.Apply(IconSet);

    // ---- about ------------------------------------------------------------------------------

    /// <summary>Where the project lives. Also the update feed.</summary>
    public static Uri ProjectUrl { get; } = new("https://github.com/jamtheradar/Muster");

    public static Uri LicenceUrl { get; } = new("https://github.com/jamtheradar/Muster/blob/main/LICENSE");

    public static Uri ReleasesUrl { get; } = new("https://github.com/jamtheradar/Muster/releases");

    public static string AppVersion { get; } = BuildInfo.Version;

    public static string BuiltOn { get; } = BuildInfo.BuiltOn;

    public static string Copyright { get; } = BuildInfo.Copyright;

    /// <summary>
    /// The WebView2 runtime actually installed, which is the first thing worth knowing when a
    /// session will not come up. Read from the static so it costs nothing and needs no
    /// environment; the runtime being absent is itself the answer.
    /// </summary>
    public static string WebViewRuntime { get; } = BuildInfo.WebViewRuntime;

    /// <summary>Where the profiles live. Deleting one of these is what forces a fresh sign-in.</summary>
    public static string ProfileFolder { get; } = BuildInfo.ProfileFolder;

    /// <summary>Whether this copy can update itself at all. False in a build folder.</summary>
    public bool CanUpdate => _updates.IsInstalled;

    /// <summary>True once an update is downloaded, which turns the button into a restart.</summary>
    [ObservableProperty]
    public partial bool UpdateReady { get; set; }

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; set; }

    [RelayCommand]
    private async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        IsCheckingForUpdates = true;
        UpdateStatus = "Checking...";

        try
        {
            UpdateStatus = await _updates.CheckAsync(ct).ConfigureAwait(true);
            UpdateReady = _updates.IsPending;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "Cancelled.";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// <summary>
    /// Closes Muster and reopens it on the new version. Every session goes with it, so this is a
    /// button the user presses rather than something that happens to them.
    /// </summary>
    [RelayCommand]
    private void RestartToUpdate()
    {
        if (!_updates.ApplyAndRestart())
        {
            UpdateStatus = "Nothing is staged to install.";
        }
    }

    [RelayCommand]
    private void OpenProject() => FileExplorer.OpenLink(ProjectUrl, _log);

    [RelayCommand]
    private void OpenLicence() => FileExplorer.OpenLink(LicenceUrl, _log);

    [RelayCommand]
    private void OpenReleases() => FileExplorer.OpenLink(ReleasesUrl, _log);

    // ---- links ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExternalLinksDescription))]
    public partial ExternalLinkPolicy ExternalLinks { get; set; }

    /// <summary>
    /// What the registered handler is right now, in the words a person would use. Reported rather
    /// than assumed, for the same reason URL Router's own setup tab reports it: an application
    /// cannot make itself the default browser, so the only honest thing to show is what Windows
    /// actually says.
    /// </summary>
    public string DefaultHandlerSummary => ExternalLinkOpener.DescribeHandler();

    public string ExternalLinksDescription => ExternalLinks switch
    {
        ExternalLinkPolicy.Never =>
            "Every link opens in a tab here, whatever is registered. The safe answer, and the way Muster behaved before this setting existed.",
        ExternalLinkPolicy.Always =>
            "Links leave Muster whatever is registered. Only worth choosing if your handler routes by profile under a name this build does not recognise - otherwise every link lands in one signed-in browser.",
        _ =>
            "Links leave Muster only while the registered handler is known to route by profile.",
    };

    // ---- suspension -------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool SuspensionEnabled { get; set; }

    [ObservableProperty]
    public partial string IdleMinutes { get; set; }

    // ---- logging ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelDescription))]
    public partial LogVerbosity Level { get; set; }

    [ObservableProperty]
    public partial string RetentionDays { get; set; }

    /// <summary>Blank means the default folder under <c>%LOCALAPPDATA%</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedLogDirectory))]
    public partial string LogDirectory { get; set; }

    public string ResolvedLogDirectory => LogFolder.Resolve(LogDirectory);

    /// <summary>Where the log is actually being written this run, whatever the pending edits say.</summary>
    public string ActiveLogDirectory => _logs.Directory;

    public string ActiveLogFile => _logs.CurrentFile;

    public string LevelDescription => Level switch
    {
        LogVerbosity.Trace => "Everything. Very noisy; for reproducing a specific fault only.",
        LogVerbosity.Debug => "Unread breakdowns, new-window routing, raw audio acquire and release. What --verbose turns on.",
        LogVerbosity.Information => "Session lifecycle, config load, presence transitions. The default.",
        LogVerbosity.Warning => "Only things that went wrong but were recovered from.",
        LogVerbosity.Error => "Only failures.",
        LogVerbosity.Critical => "Only unhandled failures.",
        _ => "Nothing is written to the log file at all.",
    };

    // ---- log files --------------------------------------------------------------------------

    /// <summary>What is in the log folder right now, newest first.</summary>
    public ObservableCollection<LogFile> LogFiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedLogFile))]
    public partial LogFile? SelectedLogFile { get; set; }

    public bool HasSelectedLogFile => SelectedLogFile is not null;

    [ObservableProperty]
    public partial string LogFolderSummary { get; set; } = string.Empty;

    // ---- state ------------------------------------------------------------------------------

    public string ConfigPath { get; }

    /// <summary>Blocking problems from the last save attempt. Nothing is written while this is non-empty.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Things worth knowing about but not worth refusing to save over.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>Whether the validation strip along the bottom is showing at all.</summary>
    [ObservableProperty]
    public partial bool ShowValidation { get; set; }

    public bool HasProblems => Problems.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>True once a save has landed that only takes effect on next launch.</summary>
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    /// <summary>Set when the config on disk could not be read, in which case nothing is editable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial string? LoadError { get; set; }

    public bool IsEditable => LoadError is null;

    /// <summary>
    /// Whether the editor holds anything not yet written. Leans on <see cref="MusterConfig"/>'s
    /// structural equality rather than tracking a dirty flag across the whole tree.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            if (_saved is null)
            {
                return false;
            }

            var problems = new List<string>();
            var built = Build(problems);

            // Input that cannot be built at all is, by definition, not what is on disk.
            return problems.Count > 0 || built != _saved;
        }
    }

    // ---- loading ----------------------------------------------------------------------------

    /// <summary>Reads muster.json into the editor. Safe to call again to discard edits.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        MusterConfig config;

        try
        {
            config = await _store.LoadAsync(ct).ConfigureAwait(true);
            LoadError = null;
        }
        catch (ConfigException ex)
        {
            // Offering a form pre-filled with defaults here would let one click overwrite a file
            // the user has merely mistyped. Send them to the file instead.
            _log.LogError(ex, "Settings could not load the config");
            LoadError = ex.Message;
            _saved = null;
            return;
        }

        _saved = config;

        Workspaces.Clear();
        foreach (var workspace in config.Workspaces)
        {
            Workspaces.Add(new WorkspaceEditorViewModel(workspace));
        }

        SelectedWorkspace = Workspaces.FirstOrDefault();

        PresenceEnabled = config.Presence.Enabled;
        CallDetectDelay = Text(config.Presence.CallDetectDelaySeconds);
        CallClearDelay = Text(config.Presence.CallClearDelaySeconds);
        ExpirationDuration = config.Presence.ExpirationDuration;
        ClientId = config.Presence.ClientId ?? string.Empty;

        RebuildPresenceAccounts(config);

        ToastsEnabled = config.Notifications.ToastsEnabled;
        HistoryLimit = Text(config.Notifications.HistoryLimit);

        IconSet = config.Appearance.IconSet;

        PinnedShortcutStatus = _pinned.Describe()
            ?? "Muster is not pinned to the taskbar, so there is no shortcut to change.";

        ExternalLinks = config.Links.External;
        OnPropertyChanged(nameof(DefaultHandlerSummary));

        SuspensionEnabled = config.Suspension.Enabled;
        IdleMinutes = Text(config.Suspension.IdleMinutes);

        Level = config.Logging.Level;
        RetentionDays = Text(config.Logging.RetentionDays);
        LogDirectory = config.Logging.Directory ?? string.Empty;

        Problems.Clear();
        Warnings.Clear();
        ShowValidation = false;
        RestartRequired = false;
        StatusMessage = string.Empty;
        RaiseValidationChanged();

        RefreshLogFiles();
    }

    /// <summary>
    /// Rebuilds the presence account list and asks MSAL which of them are signed in. Sign-in state
    /// is per tenant and lives in the token cache, not the config, so it cannot be read off disk.
    /// </summary>
    private void RebuildPresenceAccounts(MusterConfig config)
    {
        PresenceAccounts.Clear();

        foreach (var account in PresenceAccount.From(config))
        {
            PresenceAccounts.Add(new PresenceAccountViewModel(account, _tokens));
        }

        OnPropertyChanged(nameof(HasPresenceAccounts));
        OnPropertyChanged(nameof(HasNoPresenceAccounts));

        _ = RefreshSignInStateAsync();
    }

    private async Task RefreshSignInStateAsync()
    {
        foreach (var account in PresenceAccounts.ToList())
        {
            try
            {
                await account.RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not read the sign-in state of {Account}", account.Label);
            }
        }
    }

    // ---- workspace commands -----------------------------------------------------------------

    [RelayCommand]
    private void AddWorkspace()
    {
        var workspace = WorkspaceEditorViewModel.Create(SuggestWorkspaceId());
        Workspaces.Add(workspace);
        SelectedWorkspace = workspace;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedWorkspace))]
    private void RemoveWorkspace()
    {
        if (SelectedWorkspace is not { } workspace)
        {
            return;
        }

        var index = Workspaces.IndexOf(workspace);
        Workspaces.Remove(workspace);
        SelectedWorkspace = Workspaces.Count == 0 ? null : Workspaces[Math.Min(index, Workspaces.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelectedWorkspace))]
    private void MoveWorkspaceUp() => MoveWorkspace(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedWorkspace))]
    private void MoveWorkspaceDown() => MoveWorkspace(1);

    // ---- log commands -----------------------------------------------------------------------

    /// <summary>Rescans the folder the log is being written to right now.</summary>
    [RelayCommand]
    private void RefreshLogFiles()
    {
        var files = LogFolder.List(ActiveLogDirectory);

        LogFiles.Clear();
        foreach (var file in files)
        {
            LogFiles.Add(file);
        }

        SelectedLogFile = LogFiles.FirstOrDefault();

        LogFolderSummary = files.Count == 0
            ? "No log files yet."
            : $"{files.Count} file{(files.Count == 1 ? string.Empty : "s")}, " +
              $"{new LogFile(string.Empty, string.Empty, LogFolder.TotalBytes(files), default).SizeText} total.";
    }

    [RelayCommand]
    private void OpenLogFolder() => FileExplorer.OpenFolder(ActiveLogDirectory, _log);

    /// <summary>Opens today's log in whatever handles .log files.</summary>
    [RelayCommand]
    private void OpenCurrentLog() => FileExplorer.OpenFile(ActiveLogFile, _log);

    [RelayCommand(CanExecute = nameof(HasSelectedLogFile))]
    private void OpenSelectedLog()
    {
        if (SelectedLogFile is { } file)
        {
            FileExplorer.OpenFile(file.Path, _log);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedLogFile))]
    private void RevealSelectedLog()
    {
        if (SelectedLogFile is { } file)
        {
            FileExplorer.RevealFile(file.Path, _log);
        }
    }

    /// <summary>Applies the retention window now rather than waiting for the next launch.</summary>
    [RelayCommand]
    private void PruneLogsNow()
    {
        if (!int.TryParse(RetentionDays, CultureInfo.InvariantCulture, out var days))
        {
            StatusMessage = $"'{RetentionDays}' is not a number of days.";
            return;
        }

        var deleted = LogFolder.Prune(ActiveLogDirectory, days, DateTimeOffset.Now);
        RefreshLogFiles();

        StatusMessage = days <= 0
            ? "Retention is off, so nothing was deleted."
            : $"Deleted {deleted} log file{(deleted == 1 ? string.Empty : "s")} older than {days} days.";
    }

    [RelayCommand]
    private void OpenConfigFile() => FileExplorer.RevealFile(ConfigPath, _log);

    // ---- saving -----------------------------------------------------------------------------

    /// <summary>
    /// Validates and writes muster.json. Returns false without touching the file if anything does
    /// not check out, so a settings screen can never produce a config the app will refuse to load.
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        if (_saved is null)
        {
            StatusMessage = "The config could not be read, so it will not be overwritten.";
            return false;
        }

        var problems = new List<string>();
        var config = Build(problems);

        // The same validator the loader runs, so anything the settings screen accepts is
        // something the app will still start with.
        problems.AddRange(ConfigValidator.Validate(config));

        Problems.Clear();
        foreach (var problem in problems)
        {
            Problems.Add(problem);
        }

        ShowValidation = HasProblems || HasWarnings;
        RaiseValidationChanged();

        if (problems.Count > 0)
        {
            StatusMessage = $"Not saved. {problems.Count} problem{(problems.Count == 1 ? string.Empty : "s")} to fix.";
            return false;
        }

        try
        {
            await _store.SaveAsync(config, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not write the config to {Path}", ConfigPath);
            StatusMessage = $"Could not write {ConfigPath}: {ex.Message}";
            return false;
        }

        var diff = ConfigDiff.Between(_saved, config);
        _saved = config;

        // The shell picks the write up and applies it. Only the handful of things that cannot be
        // changed underneath a running app still want a relaunch.
        RestartRequired = diff.RequiresRestart;

        StatusMessage = RestartRequired
            ? $"Saved to {ConfigPath}. {diff.RestartReasons[0]}"
            : $"Saved to {ConfigPath}, and applied.";

        _log.LogInformation(
            "Settings saved: {Workspaces} workspace(s), level {Level}, restart required {Restart}",
            config.Workspaces.Count,
            config.Logging.Level,
            RestartRequired);

        RefreshLogFiles();
        return true;
    }

    /// <summary>Turns the editor back into a config record, collecting problems as it goes.</summary>
    private MusterConfig Build(ICollection<string> problems)
    {
        var workspaces = Workspaces.Select(workspace => workspace.TryBuild(problems)).ToList();

        var detect = ParseInt(CallDetectDelay, "presence: callDetectDelaySeconds", 0, 600, problems, 3);
        var clear = ParseInt(CallClearDelay, "presence: callClearDelaySeconds", 0, 600, problems, 10);
        var history = ParseInt(
            HistoryLimit,
            "notifications: historyLimit",
            1,
            5000,
            problems,
            NotificationConfig.DefaultHistoryLimit);
        var idle = ParseInt(
            IdleMinutes,
            "suspension: idleMinutes",
            1,
            SuspensionConfig.MaxIdleMinutes,
            problems,
            SuspensionConfig.DefaultIdleMinutes);
        var retention = ParseInt(
            RetentionDays,
            "logging: retentionDays",
            0,
            LoggingConfig.MaxRetentionDays,
            problems,
            LoggingConfig.DefaultRetentionDays);

        RebuildWarnings();

        return new MusterConfig
        {
            Version = _saved?.Version ?? MusterConfig.CurrentVersion,
            Workspaces = workspaces,
            Presence = new PresenceConfig
            {
                Enabled = PresenceEnabled,
                CallDetectDelaySeconds = detect,
                CallClearDelaySeconds = clear,
                ExpirationDuration = ExpirationDuration.Trim(),
                ClientId = string.IsNullOrWhiteSpace(ClientId) ? null : ClientId.Trim(),
            },
            Notifications = new NotificationConfig
            {
                ToastsEnabled = ToastsEnabled,
                HistoryLimit = history,
            },
            Appearance = new AppearanceConfig
            {
                IconSet = IconSet,
            },
            Links = new LinkConfig
            {
                External = ExternalLinks,
            },
            Suspension = new SuspensionConfig
            {
                Enabled = SuspensionEnabled,
                IdleMinutes = idle,
            },
            Logging = new LoggingConfig
            {
                Level = Level,
                RetentionDays = retention,
                Directory = string.IsNullOrWhiteSpace(LogDirectory) ? null : LogDirectory.Trim(),
            },
        };
    }

    /// <summary>
    /// Non-blocking notes. The expiry floor is the important one: preferred presence does not
    /// auto-revert, so a short window is the difference between a crash mid-call costing you two
    /// hours of showing Busy and costing you the rest of the day.
    /// </summary>
    private void RebuildWarnings()
    {
        Warnings.Clear();

        var duration = ExpirationDuration.Trim();
        if (!string.IsNullOrEmpty(duration))
        {
            try
            {
                if (XmlConvert.ToTimeSpan(duration) < TimeSpan.FromHours(2))
                {
                    Warnings.Add(
                        $"presence: {duration} is under the two-hour floor. Preferred presence never " +
                        "auto-reverts, so the expiry is the only thing that clears Busy after a crash.");
                }
            }
            catch (FormatException)
            {
                Warnings.Add($"presence: '{duration}' does not look like an ISO 8601 duration such as PT2H.");
            }
        }

        if (PresenceEnabled)
        {
            Warnings.Add(
                "Presence writes change status that people in client tenants can see. With this " +
                "on, a call in one tenant shows you Busy in every other one.");

            var unsigned = PresenceAccounts
                .Where(account => !account.IsSignedIn)
                .Select(account => account.Label)
                .ToList();

            if (unsigned.Count > 0)
            {
                Warnings.Add(
                    "Not signed in, so nothing will be written for: " + string.Join(", ", unsigned) +
                    ". Use Sign in above.");
            }
        }

        if (Level == LogVerbosity.None)
        {
            Warnings.Add("Logging is off. Nothing will be written when something goes wrong.");
        }

        if (int.TryParse(RetentionDays, CultureInfo.InvariantCulture, out var days) && days == 0)
        {
            Warnings.Add("Log retention is off, so the log folder will grow without limit.");
        }
    }

    private static int ParseInt(
        string text,
        string field,
        int minimum,
        int maximum,
        ICollection<string> problems,
        int fallback)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            problems.Add($"{field}: '{text}' is not a whole number.");
            return fallback;
        }

        if (value < minimum || value > maximum)
        {
            problems.Add($"{field}: must be between {minimum} and {maximum}.");
            return fallback;
        }

        return value;
    }

    private void MoveWorkspace(int offset)
    {
        if (SelectedWorkspace is not { } workspace)
        {
            return;
        }

        var from = Workspaces.IndexOf(workspace);
        var to = from + offset;

        if (from < 0 || to < 0 || to >= Workspaces.Count)
        {
            return;
        }

        Workspaces.Move(from, to);
        SelectedWorkspace = workspace;
    }

    private string SuggestWorkspaceId()
    {
        for (var n = 1; ; n++)
        {
            var candidate = $"workspace-{n}";
            if (!Workspaces.Any(w => string.Equals(w.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Problems and Warnings are collections, so the computed flags the strip binds to have to be
    // raised by hand whenever their contents change.
    private void RaiseValidationChanged()
    {
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasWarnings));
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceEditorViewModel? value)
    {
        RemoveWorkspaceCommand.NotifyCanExecuteChanged();
        MoveWorkspaceUpCommand.NotifyCanExecuteChanged();
        MoveWorkspaceDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLogFileChanged(LogFile? value)
    {
        OpenSelectedLogCommand.NotifyCanExecuteChanged();
        RevealSelectedLogCommand.NotifyCanExecuteChanged();
    }
}
