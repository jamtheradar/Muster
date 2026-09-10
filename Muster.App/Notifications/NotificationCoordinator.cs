using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Muster.App.Hosting;
using Muster.App.ViewModels;
using Muster.Core.Config;
using Muster.Core.Hosting;
using Muster.Core.Notifications;

namespace Muster.App.Notifications;

/// <summary>
/// Joins the two ingestion paths to the hub, the toasts and the panel, and keeps hold of the
/// live WebView2 notification objects so a click can be reported back to the page.
/// </summary>
public sealed class NotificationCoordinator : IDisposable
{
    // Only the WebView2 path has anything to report back to, and only until the page drops it.
    private readonly Dictionary<string, CoreWebView2Notification> _live = new(StringComparer.Ordinal);

    private readonly NotificationHub hub;
    private readonly ToastService toasts;
    private readonly CurrentConfig config;
    private readonly MainViewModel viewModel;
    private readonly ILogger<NotificationCoordinator> log;
    private bool _disposed;

    public NotificationCoordinator(
        NotificationHub hub,
        ToastService toasts,
        CurrentConfig config,
        MainViewModel viewModel,
        ILogger<NotificationCoordinator> log)
    {
        this.hub = hub;
        this.toasts = toasts;
        this.config = config;
        this.viewModel = viewModel;
        this.log = log;

        // Without this a toast click goes nowhere, which makes the activation argument on every
        // toast pointless.
        toasts.Activated += OnToastActivated;
    }

    /// <summary>Raised when the user asks to open the tab a notification came from.</summary>
    public event EventHandler<NotificationRecord>? ActivationRequested;

    /// <summary>
    /// Resolves a session that has no tab, so its notifications are still ingested. Floating
    /// windows are the case: a popped-out meeting is a live session with nothing in the tab strip,
    /// and dropping what it raises would lose exactly the messages the app exists to surface.
    /// </summary>
    public Func<string, SessionDescriptor?>? ResolveDetachedSession { get; set; }

    /// <summary>Takes a notification from a session and runs it through the pipeline.</summary>
    public void Handle(RaisedNotification raised)
    {
        var tab = viewModel.FindTab(raised.SessionId);
        string workspaceId;
        string displayName;

        if (tab is not null)
        {
            // Filtering happens here, at ingestion, so a muted service never reaches the history.
            if (!tab.AcceptsNotifications)
            {
                log.LogDebug("Dropped a notification from muted or disabled {Session}", raised.SessionId);
                return;
            }

            workspaceId = tab.Descriptor.WorkspaceId;
            displayName = tab.DisplayName;
        }
        else if (ResolveDetachedSession?.Invoke(raised.SessionId) is { } detached)
        {
            // A floating window carries no per-service mute of its own; it inherits the workspace
            // it was opened from and is treated as an ordinary source.
            workspaceId = detached.WorkspaceId;
            displayName = detached.Name;
        }
        else
        {
            log.LogDebug("Notification from unknown session {Session}", raised.SessionId);
            return;
        }

        var result = hub.Ingest(
            raised.SessionId,
            workspaceId,
            displayName,
            raised.Title,
            raised.Body,
            raised.Source);

        // Attach the live object even when this was the duplicate: the service worker shim often
        // beats the WebView2 event, and only the WebView2 side can report a click back.
        if (raised.Live is { } live && !_live.ContainsKey(result.Record.Id))
        {
            _live[result.Record.Id] = live;
        }

        if (!result.IsNew)
        {
            log.LogDebug(
                "Merged a duplicate {Source} notification into {Notification}",
                raised.Source,
                result.Record.Id);
            return;
        }

        log.LogInformation(
            "Notification from {Service} via {Source}: {Title}",
            result.Record.ServiceName,
            raised.Source,
            result.Record.Title);

        // Read through CurrentConfig rather than off a snapshot, so turning toasts off in
        // settings takes effect on the next notification instead of the next launch.
        if (config.Notifications.ToastsEnabled)
        {
            toasts.Show(result.Record);
        }
    }

    /// <summary>
    /// Acts on a click, from either a toast or the panel: tells the page first, then lets the
    /// shell bring the right tab forward.
    /// </summary>
    public void Activate(NotificationRecord record)
    {
        log.LogInformation(
            "Activating {Notification} from {Service}",
            record.Id,
            record.ServiceName);

        ReportClicked(record);
        ActivationRequested?.Invoke(this, record);
    }

    /// <summary>Finds a record by the id carried in a toast argument.</summary>
    public NotificationRecord? Find(string id) => hub.Find(id);

    private void OnToastActivated(object? sender, string notificationId)
    {
        if (hub.Find(notificationId) is { } record)
        {
            Activate(record);
            return;
        }

        // The toast outlived its history entry, so there is nothing precise to open. Still worth
        // surfacing the window rather than appearing to ignore the click.
        log.LogInformation("Toast {Notification} is no longer in history", notificationId);
        ActivationRequested?.Invoke(this, new NotificationRecord(
            notificationId, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, DateTimeOffset.UtcNow, NotificationSource.WebView));
    }

    // This is what makes Teams open the right chat rather than just coming to the foreground.
    private void ReportClicked(NotificationRecord record)
    {
        if (!_live.Remove(record.Id, out var live))
        {
            return;
        }

        try
        {
            // This is the call that makes Teams open the right chat.
            live.ReportClicked();
            log.LogDebug("Reported the click back to the page for {Notification}", record.Id);
        }
        catch (Exception ex)
        {
            // The page may have closed the notification already, which invalidates the object.
            log.LogDebug(ex, "Could not report a click for {Notification}", record.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        toasts.Activated -= OnToastActivated;
        _live.Clear();
    }
}
