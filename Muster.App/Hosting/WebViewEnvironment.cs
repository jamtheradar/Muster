using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Muster.App.Hosting;

/// <summary>
/// Owns the single <see cref="CoreWebView2Environment"/> shared by every session.
/// One environment means one browser process for the whole app; per-workspace isolation
/// comes from the profile name on the controller, not from separate environments.
/// </summary>
public interface IWebViewEnvironment
{
    Task<CoreWebView2Environment> GetAsync(CancellationToken ct = default);
}

public sealed class WebViewEnvironment(ILogger<WebViewEnvironment> log) : IWebViewEnvironment
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreWebView2Environment? _environment;

    /// <summary>Root for every profile's cookies, storage and cache.</summary>
    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Muster",
        "WebView2");

    public async Task<CoreWebView2Environment> GetAsync(CancellationToken ct = default)
    {
        if (_environment is not null)
        {
            return _environment;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_environment is null)
            {
                Directory.CreateDirectory(UserDataFolder);

                var options = new CoreWebView2EnvironmentOptions
                {
                    // No extensions, ever. See SPEC section 2, out of scope.
                    AreBrowserExtensionsEnabled = false,

                    // Leave OS primary account SSO off: it would bind every profile to the
                    // Windows identity, which is the opposite of the isolation model. It is
                    // the lever to pull if Conditional Access blocks embedded sign-in.
                    AllowSingleSignOnUsingOSPrimaryAccount = false,
                };

                _environment = await CoreWebView2Environment
                    .CreateAsync(browserExecutableFolder: null, userDataFolder: UserDataFolder, options: options)
                    .ConfigureAwait(true);

                log.LogInformation(
                    "WebView2 runtime {Version}, user data folder {Folder}",
                    _environment.BrowserVersionString,
                    UserDataFolder);
            }
        }
        finally
        {
            _gate.Release();
        }

        return _environment;
    }
}
