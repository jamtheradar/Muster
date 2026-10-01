using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Muster.App.Configuration;
using Muster.App.Hosting;

namespace Muster.App.Views;

/// <summary>
/// A microphone and speaker test, so the question "can anyone hear me" can be answered in a few
/// seconds rather than by leaving the meeting to run Teams' test call.
/// </summary>
/// <remarks>
/// <para>
/// The test runs in a WebView2 rather than against WASAPI directly, and that is the point rather
/// than a shortcut. Teams' audio goes through Chromium's media stack: its device enumeration, its
/// default-device choice, its capture pipeline. A native test would exercise a different path and
/// could pass while Teams stayed silent — which under RDP, where the redirected device is the
/// thing that half-works, is the likely case rather than the far-fetched one.
/// </para>
/// <para>
/// <b>This window deliberately does not use <see cref="HostedSession"/>.</b> Every hosted session
/// gets <c>bridge.js</c>, which reports microphone acquisition to the presence state machine — so
/// a test page opening the microphone through one would be read as joining a call and could set
/// the user Busy in every configured tenant. Running a plain control here keeps the whole presence
/// pipeline out of it, which is also why nothing under <c>Muster.Core/Presence</c> had to change
/// for this feature to exist.
/// </para>
/// <para>
/// It runs on a profile of its own for the same reason: the page needs microphone permission, and
/// granting that inside a workspace profile would write a permission decision into a profile the
/// user signs into client tenants with.
/// </para>
/// </remarks>
public partial class DeviceCheckWindow : Window
{
    /// <summary>
    /// The virtual host the test page is served from. Reserved TLD on purpose — it can never
    /// resolve to anything real, so the mapping cannot be shadowed by a name that does.
    /// </summary>
    private const string VirtualHost = "devicecheck.muster.invalid";

    /// <summary>
    /// Kept apart from every workspace profile. Nothing signs in here, so it holds nothing but a
    /// microphone permission decision.
    /// </summary>
    private const string ProfileName = "devicecheck";

    private readonly IWebViewEnvironment _environment;
    private readonly AppIcons _icons;
    private readonly ILogger<DeviceCheckWindow> _log;
    private WebView2? _view;

    public DeviceCheckWindow(
        IWebViewEnvironment environment,
        AppIcons icons,
        ILogger<DeviceCheckWindow> log)
    {
        InitializeComponent();

        _environment = environment;
        _icons = icons;
        _log = log;

        // Windows that inherit are the ones that visibly disagree with appearance.iconSet.
        Icon = _icons.CurrentWindowIcon;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // async void, so nothing above catches this. A device check that cannot start is
            // worth a visible line rather than an empty window.
            _log.LogError(ex, "Could not start the device check");
            StatusText.Text = "Could not start the device check. See the log.";
        }
    }

    private async Task StartAsync()
    {
        var environment = await _environment.GetAsync().ConfigureAwait(true);

        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        options.IsInPrivateModeEnabled = false;

        // In the visual tree and realised before EnsureCoreWebView2Async, which needs a window
        // handle — the same order every other WebView2 in the shell is created in.
        _view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x17, 0x17, 0x1A) };
        PageHost.Children.Add(_view);

        await _view.EnsureCoreWebView2Async(environment, options).ConfigureAwait(true);

        var core = _view.CoreWebView2
            ?? throw new InvalidOperationException("CoreWebView2 was not created for the device check.");

        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;

        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "devicecheck");

        if (!Directory.Exists(folder))
        {
            // Copied beside the exe by the build, like bridge.js. Missing means a broken deploy,
            // and saying so beats a blank window and a navigation error in the log.
            _log.LogError("The device check page is missing from {Folder}", folder);
            StatusText.Text = $"The device check page is missing from {folder}.";
            return;
        }

        // Before the first navigation. DenyCors because the page needs nothing from anywhere:
        // no network, no fonts, no CDN. It is one file and the Web Audio API.
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            folder,
            CoreWebView2HostResourceAccessKind.DenyCors);

        core.PermissionRequested += OnPermissionRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;

        core.Navigate($"https://{VirtualHost}/device-check.html");
        _log.LogInformation("Device check open on profile {Profile}", ProfileName);
    }

    /// <summary>
    /// Grants whatever the test page asks for, and only to the test page.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="HostedSession"/>'s allow list, and safely so: the only origin that
    /// can reach this handler is a file shipped inside the app, served from a host name that
    /// cannot resolve, on a profile nothing signs into. Anything else is refused outright rather
    /// than prompted — there is no legitimate second origin in this window, so a prompt would
    /// only ever be a question the user cannot usefully answer.
    /// </remarks>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        var ours = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && uri.Host.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase);

        e.State = ours ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;

        // SavesInProfile, so the microphone prompt does not come back every time the window is
        // opened — which for a check meant to take seconds would be most of the seconds.
        e.SavesInProfile = ours;

        _log.LogInformation(
            "Device check {Decision} {Kind} for {Uri}",
            ours ? "granted" : "refused",
            e.PermissionKind,
            e.Uri);
    }

    /// <summary>
    /// Nothing in this window opens a window. The page is local and has no links, so a request
    /// here means something has gone wrong rather than that the user asked for something.
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _log.LogWarning("Device check blocked a new window for {Uri}", e.Uri);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.LogError(
            "Device check WebView2 process failed: {Kind} / {Reason}",
            e.ProcessFailedKind,
            e.Reason);

        StatusText.Text = "The device check page stopped responding. Close and reopen it.";
    }

    /// <summary>
    /// Puts what the page found into the log, so a check run during a problem leaves a record
    /// that outlives the window. Device names only — there is nothing else here to leak.
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kind)
                || kind.GetString() != "device-check")
            {
                return;
            }

            var what = Read(root, "event");
            var detail = Read(root, "detail");

            _log.LogInformation("DEVICE CHECK {Event}: {Detail}", what, detail);
            StatusText.Text = string.IsNullOrEmpty(detail) ? what : $"{what} — {detail}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring an unreadable device check message");
        }

        static string Read(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Disposed rather than left to finalisation: this control holds a microphone open until
        // its renderer goes, and a test window that keeps the device after you close it is worse
        // than no test window.
        if (_view is { } view)
        {
            if (view.CoreWebView2 is { } core)
            {
                core.PermissionRequested -= OnPermissionRequested;
                core.WebMessageReceived -= OnWebMessageReceived;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ProcessFailed -= OnProcessFailed;
            }

            PageHost.Children.Remove(view);
            view.Dispose();
            _view = null;
        }

        base.OnClosed(e);
    }
}
