using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Muster.Core.Hosting;
using Muster.Core.Presence;

namespace Muster.App.Hosting;

/// <summary>
/// Owns every live <see cref="HostedSession"/> and the panel their controls live in.
/// </summary>
/// <remarks>
/// Switching sessions only ever changes <see cref="UIElement.Visibility"/>. Inactive sessions use
/// <see cref="Visibility.Hidden"/> rather than <see cref="Visibility.Collapsed"/> on purpose:
/// WebView2 is an HwndHost, and a collapsed element is never arranged, so its child window is
/// never created. Hidden keeps the session alive and loading in the background.
/// </remarks>
public sealed class SessionManager(
    IWebViewEnvironment environment,
    DiagnosticOptions diagnostics,
    TeamsPresenceTracker presence,
    ILoggerFactory loggerFactory) : IDisposable
{
    private readonly Dictionary<string, HostedSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<HostedSession>> _starting = new(StringComparer.Ordinal);

    // Where each session's control actually lives. Almost always the shared panel, but a floating
    // session sits in its own window, and teardown has to remove it from the right parent.
    private readonly Dictionary<string, Panel> _hosts = new(StringComparer.Ordinal);
    private readonly ILogger<SessionManager> _log = loggerFactory.CreateLogger<SessionManager>();
    private Panel? _host;

    /// <summary>Raised when any live session's document title changes.</summary>
    public event EventHandler<SessionTitleChangedEventArgs>? TitleChanged;

    /// <summary>Raised when any live session's page asks for a new window.</summary>
    public event EventHandler<NewWindowRequest>? NewWindowRequested;

    /// <summary>Raised when a page calls <c>window.close()</c> on itself.</summary>
    public event EventHandler<SessionDescriptor>? CloseRequested;

    /// <summary>Raised for every notification from every live session, before deduplication.</summary>
    public event EventHandler<RaisedNotification>? NotificationRaised;

    /// <summary>Raised when any live session starts or stops capturing audio.</summary>
    public event EventHandler<MediaStateChangedEventArgs>? MediaStateChanged;

    /// <summary>Raised once a session is live, whether it was shown, preloaded or adopted.</summary>
    public event EventHandler<SessionDescriptor>? SessionStarted;

    /// <summary>Raised when a session is torn down, so pending state can be released.</summary>
    public event EventHandler<SessionDescriptor>? SessionRemoved;

    /// <summary>The session currently visible in the content area.</summary>
    public HostedSession? Active { get; private set; }

    public int LiveCount => _sessions.Count;

    /// <summary>How many live sessions are currently asleep. Shown in the status bar.</summary>
    public int SuspendedCount => _sessions.Values.Count(session => session.IsSuspended);

    /// <summary>Every running session's id, for callers that hold per-session state alongside.</summary>
    public IReadOnlyCollection<string> LiveSessionIds => _sessions.Keys;

    /// <summary>Binds the manager to the panel that hosts session controls.</summary>
    public void Attach(Panel host) => _host = host;

    /// <summary>
    /// The live session for an id, or null if it has not been started. Hot-reload uses this to
    /// tell a session whose URL changed to navigate, rather than tearing it down and losing auth.
    /// </summary>
    public HostedSession? Find(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    /// <summary>
    /// Creates the session if it does not exist yet, then makes it the only visible one.
    /// </summary>
    public async Task<HostedSession> ShowAsync(SessionDescriptor descriptor, CancellationToken ct = default)
    {
        var session = await GetOrCreateAsync(descriptor, ct).ConfigureAwait(true);

        // Before the visibility flip, not after. WebView2 wakes a session on its own once the
        // control becomes visible, but doing it here means the page is already running by the time
        // it is arranged, rather than repainting a frozen frame first.
        session.Resume();

        foreach (var candidate in _sessions.Values)
        {
            // A floating session is in its own window. Its visibility belongs to that window, and
            // hiding it here would blank a popped-out meeting the moment you changed tabs.
            if (candidate.Descriptor.IsFloating)
            {
                continue;
            }

            candidate.View.Visibility = ReferenceEquals(candidate, session)
                ? Visibility.Visible
                : Visibility.Hidden;
        }

        Active = session;
        return session;
    }

    /// <summary>
    /// Brings a session up without showing it, so it can receive notifications from the start.
    /// Used for <c>keepAlive</c> services at launch.
    /// </summary>
    public async Task PreloadAsync(SessionDescriptor descriptor, CancellationToken ct = default)
        => await GetOrCreateAsync(descriptor, ct).ConfigureAwait(true);

    /// <summary>
    /// Creates a session that has not navigated anywhere, ready to be handed to a page as its new
    /// window. It must not navigate first: WebView2 rejects an already-navigated control as
    /// <c>e.NewWindow</c>.
    /// </summary>
    /// <param name="host">
    /// Where the control should live. Defaults to the shared session panel; a floating window
    /// passes its own, and must already be on screen — WebView2 needs a realised window handle
    /// before <c>EnsureCoreWebView2Async</c>, and rejects a control that has already navigated.
    /// </param>
    public Task<HostedSession> CreateForAdoptionAsync(
        SessionDescriptor descriptor,
        Panel? host = null,
        CancellationToken ct = default)
        => GetOrCreateAsync(descriptor, ct, navigateHome: false, host);

    /// <summary>
    /// Puts one session to sleep, if it exists and will go. Answers
    /// <see cref="Muster.Core.Hosting.SuspensionCoordinator.SuspendRequested"/>.
    /// </summary>
    public async Task<bool> TrySuspendAsync(string sessionId)
        => Find(sessionId) is { } session && await session.TrySuspendAsync().ConfigureAwait(true);

    /// <summary>Wakes one session if it is asleep. A no-op otherwise.</summary>
    public void Resume(string sessionId) => Find(sessionId)?.Resume();

    /// <summary>
    /// Brings a session up inside a caller-supplied panel and navigates it to its home URL. Used
    /// by pop-out, where there is no page waiting to adopt the control and it is the shell that
    /// decides where to go.
    /// </summary>
    public Task<HostedSession> CreateDetachedAsync(
        SessionDescriptor descriptor,
        Panel host,
        CancellationToken ct = default)
        => GetOrCreateAsync(descriptor, ct, navigateHome: true, host);

    /// <summary>Tears down a session and removes its control. Used when an ephemeral tab closes.</summary>
    public void Remove(string sessionId)
    {
        if (!_sessions.Remove(sessionId, out var session))
        {
            return;
        }

        Unsubscribe(session);
        HostOf(sessionId).Children.Remove(session.View);
        _hosts.Remove(sessionId);

        if (ReferenceEquals(Active, session))
        {
            Active = null;
        }

        session.Dispose();
        // A token outliving the tab that produced it is worth nothing and worth keeping even less.
        presence.Forget(sessionId);

        SessionRemoved?.Invoke(this, session.Descriptor);
        _log.LogInformation("Session {Session} closed, {Count} still running", sessionId, _sessions.Count);
    }

    /// <summary>
    /// Everything here runs on the UI thread, so no locking, but two callers can still interleave
    /// across the initialisation await: the active tab and the keep-alive preload routinely ask
    /// for the same session at once. Handing both the same in-flight task means one session per
    /// id, and no caller ever sees one whose CoreWebView2 is not ready yet.
    /// </summary>
    private Task<HostedSession> GetOrCreateAsync(
        SessionDescriptor descriptor,
        CancellationToken ct,
        bool navigateHome = true,
        Panel? host = null)
    {
        if (_sessions.TryGetValue(descriptor.Id, out var existing))
        {
            return Task.FromResult(existing);
        }

        if (_starting.TryGetValue(descriptor.Id, out var inFlight))
        {
            return inFlight;
        }

        var task = CreateAsync(descriptor, ct, navigateHome, host);
        _starting[descriptor.Id] = task;
        return task;
    }

    private async Task<HostedSession> CreateAsync(
        SessionDescriptor descriptor,
        CancellationToken ct,
        bool navigateHome,
        Panel? into)
    {
        var host = into
            ?? _host
            ?? throw new InvalidOperationException("SessionManager.Attach was never called.");

        var session = new HostedSession(descriptor, environment, diagnostics, loggerFactory);

        // Carries a live bearer token, so it goes straight into the tracker and nowhere else.
        session.PresenceRequestObserved += (_, observation) => presence.Observe(observation);

        // Hidden, so a new tab does not flash over the one being replaced. A floating session owns
        // its window outright and is the only thing in it, so it starts visible.
        session.View.Visibility = descriptor.IsFloating ? Visibility.Visible : Visibility.Hidden;
        session.TitleChanged += OnSessionTitleChanged;
        session.NewWindowRequested += OnSessionNewWindowRequested;
        session.CloseRequested += OnSessionCloseRequested;
        session.NotificationRaised += OnSessionNotificationRaised;
        session.MediaStateChanged += OnSessionMediaStateChanged;

        host.Children.Add(session.View);

        try
        {
            await session.InitialiseAsync(navigateHome, ct).ConfigureAwait(true);
        }
        catch (Exception)
        {
            Unsubscribe(session);
            host.Children.Remove(session.View);
            session.Dispose();
            throw;
        }
        finally
        {
            _starting.Remove(descriptor.Id);
        }

        _sessions[descriptor.Id] = session;
        _hosts[descriptor.Id] = host;

        _log.LogInformation(
            "Session {Session} live on profile {Profile}, {Count} session(s) running",
            descriptor.Id,
            descriptor.ProfileName,
            _sessions.Count);

        SessionStarted?.Invoke(this, descriptor);
        return session;
    }

    private void OnSessionTitleChanged(object? sender, string title)
    {
        if (sender is HostedSession session)
        {
            TitleChanged?.Invoke(this, new SessionTitleChangedEventArgs(session.Descriptor.Id, title));
        }
    }

    private void OnSessionNewWindowRequested(object? sender, NewWindowRequest request)
    {
        if (NewWindowRequested is { } handler)
        {
            handler(this, request);
            return;
        }

        // Nothing is listening. Complete the request so the originating page is not left hanging.
        _log.LogWarning("New window for {Uri} had no listener", request.Target);
        request.Complete();
    }

    private void OnSessionCloseRequested(object? sender, EventArgs e)
    {
        if (sender is HostedSession session)
        {
            CloseRequested?.Invoke(this, session.Descriptor);
        }
    }

    private void OnSessionNotificationRaised(object? sender, RaisedNotification notification)
        => NotificationRaised?.Invoke(this, notification);

    private void OnSessionMediaStateChanged(object? sender, MediaStateChangedEventArgs e)
        => MediaStateChanged?.Invoke(this, e);

    /// <summary>The panel a session's control was added to, falling back to the shared one.</summary>
    private Panel HostOf(string sessionId)
        => _hosts.TryGetValue(sessionId, out var host) ? host : _host ?? new Grid();

    private void Unsubscribe(HostedSession session)
    {
        session.TitleChanged -= OnSessionTitleChanged;
        session.NewWindowRequested -= OnSessionNewWindowRequested;
        session.CloseRequested -= OnSessionCloseRequested;
        session.NotificationRaised -= OnSessionNotificationRaised;
        session.MediaStateChanged -= OnSessionMediaStateChanged;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            Unsubscribe(session);
            HostOf(session.Descriptor.Id).Children.Remove(session.View);
            session.Dispose();
        }

        _sessions.Clear();
        _hosts.Clear();
        _starting.Clear();
        Active = null;
    }
}
