using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Muster.Core.Hosting;
using Muster.Core.Notifications;
using Muster.Core.Presence;

namespace Muster.App.Hosting;

/// <summary>
/// One live web session: a <see cref="WebView2"/> control bound to a named profile inside the
/// shared environment, with the host behaviours from SPEC section 6 wired up.
/// </summary>
/// <remarks>
/// The control is created once and lives for as long as the session does. Switching between
/// sessions is done with <see cref="UIElement.Visibility"/>, never by reassigning
/// <see cref="WebView2.Source"/>, which would tear down auth and notifications.
/// </remarks>
public sealed class HostedSession : IHostedSession, IDisposable
{
    private readonly IWebViewEnvironment _environment;
    private readonly DiagnosticOptions _diagnostics;
    private readonly ILogger _log;
    private bool _disposed;

    public HostedSession(
        SessionDescriptor descriptor,
        IWebViewEnvironment environment,
        DiagnosticOptions diagnostics,
        ILoggerFactory loggerFactory)
    {
        Descriptor = descriptor;
        _environment = environment;
        _diagnostics = diagnostics;
        _log = loggerFactory.CreateLogger($"Session:{descriptor.Id}");
        View = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
    }

    public SessionDescriptor Descriptor { get; }

    public WebView2 View { get; }

    public CoreWebView2? Core => View.CoreWebView2;

    /// <summary>The page's current title, or null before the session is up.</summary>
    public string? DocumentTitle => Core?.DocumentTitle;

    /// <summary>
    /// Where the session actually is now, which is not its configured home once the user has
    /// navigated. Null before the session is up, or if the page is somewhere unparseable.
    /// </summary>
    public Uri? CurrentUri => Uri.TryCreate(Core?.Source, UriKind.Absolute, out var parsed) ? parsed : null;

    /// <summary>Raised when the page title changes. Milestone 4 parses this for unread counts.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Raised when the session navigates. Informational, for the status bar.</summary>
    public event EventHandler<string>? UriChanged;

    /// <summary>Raised when the page asks its host window to close. Ephemeral tabs only.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the page wants a new window. The shell decides where it goes.</summary>
    public event EventHandler<NewWindowRequest>? NewWindowRequested;

    /// <summary>Raised for a notification from either ingestion path, before deduplication.</summary>
    public event EventHandler<RaisedNotification>? NotificationRaised;

    /// <summary>Raised when this session starts or stops capturing audio. The call detector.</summary>
    public event EventHandler<MediaStateChangedEventArgs>? MediaStateChanged;

    /// <summary>
    /// Raised for every presence request this session makes, so the page strategy can learn how to
    /// speak as it. Carries live secrets: handlers must not log or persist what is in it.
    /// </summary>
    public event EventHandler<TeamsPresenceObservation>? PresenceRequestObserved;


    /// <summary>
    /// Creates the underlying CoreWebView2 on the shared environment under this session's profile.
    /// The control must already be in a loaded visual tree, since it needs a window handle.
    /// </summary>
    /// <param name="navigateHome">
    /// False for sessions handed to NewWindowRequested: WebView2 requires that a control offered
    /// as e.NewWindow has not navigated yet.
    /// </param>
    public async Task InitialiseAsync(bool navigateHome = true, CancellationToken ct = default)
    {
        var environment = await _environment.GetAsync(ct).ConfigureAwait(true);

        var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
        controllerOptions.ProfileName = Descriptor.ProfileName;
        controllerOptions.IsInPrivateModeEnabled = false;

        await View.EnsureCoreWebView2Async(environment, controllerOptions).ConfigureAwait(true);

        var core = View.CoreWebView2
            ?? throw new InvalidOperationException($"CoreWebView2 was not created for session {Descriptor.Id}.");

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        // Before any navigation, or the wrappers miss the first document.
        await core
            .AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript.Value)
            .ConfigureAwait(true);

        // Deliberately not on a session created for adoption. A control offered as e.NewWindow has
        // to be untouched, and WebView2's complaint when it is not is an unhelpful "value does not
        // fall within the expected range" — whether a resource filter counts as touching it is
        // unconfirmed, so this is a precaution rather than a diagnosis. It costs nothing either
        // way: an adopted session is ephemeral or floating, so it can never be a presence account,
        // because those key on service id and only a pinned tab has one.
        if (navigateHome)
        {
            if (_diagnostics.ProbePresence)
            {
                await core
                    .AddScriptToExecuteOnDocumentCreatedAsync(PresenceProbeScript.Value)
                    .ConfigureAwait(true);

                AttachPresenceInterceptor(core);

                _log.LogWarning("Presence probe injected. Read only, but do not leave it running.");
            }
            else
            {
                // Same interception, without the logging: the probe exists to describe the API,
                // this exists to use it. Attached for every pinned session rather than only Teams
                // ones on the page strategy, because the filter matches /ups/ and nothing else
                // calls it — and because tying interception to a config value would mean switching
                // a service to the page strategy did nothing until the next restart.
                AttachPresenceFilter(core);
                core.WebResourceRequested += OnPresenceRequestObserved;
            }
        }

        core.WebMessageReceived += OnWebMessageReceived;
        core.NotificationReceived += OnNotificationReceived;
        core.PermissionRequested += OnPermissionRequested;
        core.ScreenCaptureStarting += OnScreenCaptureStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WindowCloseRequested += OnWindowCloseRequested;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.SourceChanged += OnSourceChanged;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;

        _log.LogInformation(
            "Session ready on profile {Profile}, browser pid {Pid}",
            core.Profile.ProfileName,
            core.BrowserProcessId);

        if (navigateHome)
        {
            core.Navigate(Descriptor.Home.ToString());
        }
    }

    /// <summary>True while this session is asleep. Read from WebView2, never cached.</summary>
    /// <remarks>
    /// Nothing here owns this state. WebView2 resumes on its own when the control becomes visible
    /// and for some navigations, so a local flag would go stale without anything telling us.
    /// </remarks>
    public bool IsSuspended => Core?.IsSuspended ?? false;

    /// <summary>
    /// Puts the session to sleep to give back its renderer's memory. Idempotent, and a no-op
    /// rather than an error whenever suspension is not currently legal.
    /// </summary>
    /// <returns>True if the session went to sleep as a result of this call.</returns>
    /// <remarks>
    /// Two conditions matter. The controller must be invisible or the call throws
    /// <c>ERROR_INVALID_STATE</c> — the WPF control forwards <see cref="UIElement.IsVisible"/>
    /// through to it, so that is what is checked. And suspension is best effort: WebView2 declines
    /// for a page holding audio, a live download and the rest of the sleeping-tabs conditions, and
    /// reports that as false rather than as a failure.
    /// </remarks>
    public async Task<bool> TrySuspendAsync()
    {
        if (Core is not { } core || core.IsSuspended || View.IsVisible)
        {
            return false;
        }

        try
        {
            var slept = await core.TrySuspendAsync().ConfigureAwait(true);

            if (slept)
            {
                _log.LogDebug("Suspended {Session}", Descriptor.Id);
            }

            return slept;
        }
        catch (Exception ex)
        {
            // A session that will not sleep is a memory cost, never a correctness problem, so this
            // is worth a line in the log and nothing more.
            _log.LogDebug(ex, "Could not suspend {Session}", Descriptor.Id);
            return false;
        }
    }

    /// <summary>Wakes a sleeping session. Safe to call on one that is already awake.</summary>
    public void Resume()
    {
        if (Core is not { IsSuspended: true } core)
        {
            return;
        }

        try
        {
            core.Resume();
            _log.LogDebug("Resumed {Session}", Descriptor.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not resume {Session}", Descriptor.Id);
        }
    }

    /// <summary>Reloads the current page.</summary>
    public void Reload() => Core?.Reload();

    /// <summary>Returns to the session's configured home URL.</summary>
    public void GoHome() => Core?.Navigate(Descriptor.Home.ToString());

    /// <summary>Navigates this session to <paramref name="target"/>.</summary>
    public void Navigate(Uri target) => Core?.Navigate(target.ToString());

    // Non-persistent notifications only. Anything a service worker raises never reaches this
    // event, which is why bridge.js shims showNotification as well.
    private void OnNotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        try
        {
            // Handled must be set before any Report* call, or they fail with ERROR_INVALID_STATE,
            // and it cannot be un-set. Setting it also stops WebView2 drawing its own popup.
            e.Handled = true;

            var notification = e.Notification;
            notification.ReportShown();

            NotificationRaised?.Invoke(this, new RaisedNotification(
                Descriptor.Id,
                notification.Title ?? string.Empty,
                notification.Body ?? string.Empty,
                notification.Tag,
                NotificationSource.WebView,
                notification));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to handle a notification");
        }
    }

    // Messages from bridge.js. Everything here is untrusted page input.
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kind))
            {
                return;
            }

            switch (kind.GetString())
            {
                case "notification":
                    NotificationRaised?.Invoke(this, new RaisedNotification(
                        Descriptor.Id,
                        ReadString(root, "title"),
                        ReadString(root, "body"),
                        ReadString(root, "tag"),
                        NotificationSource.ServiceWorker,
                        live: null));
                    break;

                case "presence-probe":
                    // Header names only, never values: a bearer token lives in one of them.
                    _log.LogInformation(
                        "PRESENCE PROBE {How} {Method} {Status} {Url} body={Body} headers=[{Headers}]",
                        ReadString(root, "how"),
                        ReadString(root, "method"),
                        ReadNumber(root, "status"),
                        ReadString(root, "url"),
                        ReadString(root, "body"),
                        ReadString(root, "headers"));
                    break;

                case "presence-frame":
                    // Never the frame itself: that socket carries chat. Only availability-shaped
                    // values and JSON key names, which are structure rather than anyone's content.
                    _log.LogInformation(
                        "PRESENCE FRAME {How} {Bytes}b values=[{Values}] keys=[{Keys}]",
                        ReadString(root, "how"),
                        ReadNumber(root, "status"),
                        ReadString(root, "values"),
                        ReadString(root, "headers"));
                    break;

                case "media":
                    var acquired = ReadString(root, "state") == "acquired";
                    _log.LogDebug("Audio {State}", acquired ? "acquired" : "released");
                    MediaStateChanged?.Invoke(this, new MediaStateChangedEventArgs(
                        Descriptor.Id,
                        acquired,
                        ReadBool(root, "video")));
                    break;
            }
        }
        catch (Exception ex)
        {
            // A malformed message from a page must never take the session down.
            _log.LogWarning(ex, "Ignoring an unreadable bridge message");
        }
    }

    private static int ReadNumber(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // Everything a Teams tab needs to work, granted silently for trusted origins. Anything not on
    // the list falls through to the WebView2 prompt.
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (AutoGranted.Contains(e.PermissionKind) && IsTrustedOrigin(e.Uri))
        {
            // SavesInProfile writes the decision into the profile, so the prompt does not come
            // back next launch. It also overrides a Block the user clicked before this list grew.
            e.State = CoreWebView2PermissionState.Allow;
            e.SavesInProfile = true;
            _log.LogInformation("Granted {Kind} to {Uri}", e.PermissionKind, e.Uri);
            return;
        }

        _log.LogInformation("Prompting for {Kind} from {Uri}", e.PermissionKind, e.Uri);
    }

    /// <summary>
    /// Permissions granted without asking on a trusted origin. Every one of these is something
    /// Teams asks for in normal use, and a modal prompt in front of a tab you are already signed
    /// into is noise rather than a decision:
    /// <list type="bullet">
    /// <item>microphone, camera and notifications — the reason for hosting Teams at all;</item>
    /// <item>window management — "manage windows on all your displays", asked the first time a
    /// meeting or a popped-out chat wants a second monitor;</item>
    /// <item>clipboard read — pasting an image into a chat;</item>
    /// <item>local fonts and autoplay — Loop and Whiteboard rendering, and meeting join sounds;</item>
    /// <item>multiple automatic downloads — saving a chat's attachments in one go.</item>
    /// </list>
    /// Geolocation, file system read/write and the sensor kinds stay off this list deliberately:
    /// they are rare enough that a prompt is the right answer.
    /// </summary>
    private static readonly HashSet<CoreWebView2PermissionKind> AutoGranted =
    [
        CoreWebView2PermissionKind.Microphone,
        CoreWebView2PermissionKind.Camera,
        CoreWebView2PermissionKind.Notifications,
        CoreWebView2PermissionKind.WindowManagement,
        CoreWebView2PermissionKind.ClipboardRead,
        CoreWebView2PermissionKind.LocalFonts,
        CoreWebView2PermissionKind.Autoplay,
        CoreWebView2PermissionKind.MultipleAutomaticDownloads,
    ];

    // Teams screen share is a hard requirement. Never cancel this.
    private void OnScreenCaptureStarting(object? sender, CoreWebView2ScreenCaptureStartingEventArgs e)
    {
        e.Handled = true;
        e.Cancel = false;
        _log.LogInformation("Screen capture starting for {Uri}", Core?.Source);
    }

    // A link that escapes to the default browser lands in the wrong identity, which is the exact
    // problem this app exists to solve. Where it goes is the shell's decision; this hands the
    // request over and keeps the page waiting until the shell has answered.
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        e.Handled = true;

        try
        {
            if (NewWindowRequested is not { } router)
            {
                _log.LogWarning("No route for {Uri}: dropping it rather than leaking to the browser", e.Uri);
                return;
            }

            var request = new NewWindowRequest(this, e);
            router(this, request);
            await request.Completion.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Leaving Handled true with no NewWindow drops the popup. Worse than a working link,
            // far better than handing the URL to the system browser.
            _log.LogError(ex, "Failed to route new window for {Uri}", e.Uri);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnWindowCloseRequested(object? sender, object e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnDocumentTitleChanged(object? sender, object e)
        => TitleChanged?.Invoke(this, Core?.DocumentTitle ?? string.Empty);

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        => UriChanged?.Invoke(this, Core?.Source ?? string.Empty);

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            return;
        }

        // Conditional Access failures surface here. Worth a loud log line on day one.
        _log.LogWarning(
            "Navigation to {Uri} failed: {Status}, http {HttpStatus}",
            Core?.Source,
            e.WebErrorStatus,
            e.HttpStatusCode);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        => _log.LogError(
            "WebView2 process failed: {Kind} / {Reason} ({Description})",
            e.ProcessFailedKind,
            e.Reason,
            e.ProcessDescription);

    /// <summary>
    /// Watches Teams' own presence traffic from the host side rather than from page script.
    /// </summary>
    /// <remarks>
    /// The page-level wrappers in <c>probe-presence.js</c> saw nothing, while their heartbeat
    /// reported thirty other requests — so the wrappers ran and Teams simply does not route /ups/
    /// through the top document's fetch or XHR. The requests come back as source=Document from a
    /// child frame, which is why only a host-side filter with
    /// <c>CoreWebView2WebResourceRequestSourceKinds.All</c> catches them. Response interception
    /// was tried and removed: it fires only for the main frame, so it saw every CDN asset and not
    /// one /ups/ call.
    /// </remarks>
    private void AttachPresenceInterceptor(CoreWebView2 core)
    {
        AttachPresenceFilter(core);
        core.WebResourceRequested += OnPresenceResourceRequested;
    }

    /// <summary>
    /// Reports a session's presence requests so the page strategy can reuse the way in.
    /// </summary>
    /// <remarks>
    /// Header values are read here and handed straight on. They are never logged: one of them is
    /// a live bearer token, and the log is a file the user is encouraged to open and share.
    /// </remarks>
    private void OnPresenceRequestObserved(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (PresenceRequestObserved is not { } handler)
        {
            return;
        }

        try
        {
            var request = e.Request;

            if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri))
            {
                return;
            }

            // The body is read for getpresence and nothing else. Reading means draining WebView2's
            // one-shot stream and assigning a replacement — a mutation of a live request, on a
            // path Teams uses constantly — and the only thing any other body could tell us is
            // nothing. Narrow it to where it earns its keep.
            var body = uri.AbsolutePath.Contains("/presence/getpresence", StringComparison.OrdinalIgnoreCase)
                ? ReadBodyForDiagnostics(request)
                : null;

            handler(this, new TeamsPresenceObservation(
                Descriptor.Id,
                uri,
                Header(request, "authorization"),
                Header(request, "Cookie"),
                body));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not observe a presence request");
        }
    }

    private static string? Header(CoreWebView2WebResourceRequest request, string name)
        => request.Headers.Contains(name) ? request.Headers.GetHeader(name) : null;

    private void AttachPresenceFilter(CoreWebView2 core)
    {
        try
        {
            core.AddWebResourceRequestedFilter(
                "*://*/ups/*",
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
        }
        catch (NotImplementedException)
        {
            // Older runtimes have no source-kinds overload. Document requests only, which is
            // still more than the page script managed.
            core.AddWebResourceRequestedFilter("*://*/ups/*", CoreWebView2WebResourceContext.All);
            _log.LogWarning("This WebView2 cannot filter worker requests; watching documents only");
        }
    }

    private void OnPresenceResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var request = e.Request;

            // Header names only. The bearer token is in one of the values, and this goes to a file.
            var names = string.Join(", ", request.Headers.Select(header => header.Key));

            _log.LogInformation(
                "PRESENCE HTTP {Method} {Uri} source={Source} body={Body} headers=[{Headers}]",
                request.Method,
                request.Uri,
                e.RequestedSourceKind,
                ReadBodyForDiagnostics(request),
                names);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not report a presence request");
        }
    }

    /// <summary>
    /// Reads a request body and puts it back. Only ever called under the probe flag, and only for
    /// /ups/ traffic, whose payloads are status words rather than anyone's content.
    /// </summary>
    /// <remarks>
    /// The stream WebView2 hands over is forward-only, so the first attempt reported every body as
    /// unseekable and told us nothing. Draining it into memory and assigning a fresh stream back
    /// is what makes it readable without costing the request — the whole point being to capture
    /// the payload of the reset call, which is the one part of the contract still unknown.
    /// </remarks>
    private static string ReadBodyForDiagnostics(CoreWebView2WebResourceRequest request)
    {
        var content = request.Content;

        if (content is null)
        {
            return string.Empty;
        }

        try
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();

            // Hand the request a readable copy, or we have just eaten its body.
            request.Content = new MemoryStream(bytes);

            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            var body = System.Text.Encoding.UTF8.GetString(bytes);
            return body.Length > 300 ? body[..300] + "..." : body;
        }
        catch (Exception)
        {
            return "[unreadable]";
        }
    }

    // Read once: the same script goes into every session.
    private static readonly Lazy<string> BridgeScript = new(() => ReadInjected("bridge.js"));

    private static readonly Lazy<string> PresenceProbeScript = new(() => ReadInjected("probe-presence.js"));

    private static string ReadInjected(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "inject", name));

    private bool IsTrustedOrigin(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        // http is only ever trusted for loopback, where the browser already treats the origin as
        // secure. Anything else must be https before it gets a silent grant.
        if (parsed.Scheme != Uri.UriSchemeHttps && !parsed.IsLoopback)
        {
            return false;
        }

        return Matches(parsed.Host, TrustedSuffixes)
            || Matches(parsed.Host, Descriptor.AllowedPermissionOrigins);
    }

    private static bool Matches(string host, IReadOnlyList<string> suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Origins that get every kind in <see cref="AutoGranted"/> without asking.
    /// <c>cloud.microsoft</c> is the one that matters in practice: teams.microsoft.com redirects
    /// to teams.cloud.microsoft, and it is that origin, not the legacy one, that requests media.
    /// </summary>
    private static readonly string[] TrustedSuffixes =
    [
        "cloud.microsoft",
        "microsoft.com",
        "office.com",
        "live.com",
    ];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (View.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.WebResourceRequested -= OnPresenceResourceRequested;
            core.WebResourceRequested -= OnPresenceRequestObserved;
            core.NotificationReceived -= OnNotificationReceived;
            core.PermissionRequested -= OnPermissionRequested;
            core.ScreenCaptureStarting -= OnScreenCaptureStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WindowCloseRequested -= OnWindowCloseRequested;
            core.DocumentTitleChanged -= OnDocumentTitleChanged;
            core.SourceChanged -= OnSourceChanged;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.ProcessFailed -= OnProcessFailed;
        }

        View.Dispose();
    }
}
