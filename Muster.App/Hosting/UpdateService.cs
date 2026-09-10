using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Muster.App.Hosting;

/// <summary>
/// Checks GitHub Releases for a newer Muster and stages it. Applying happens on the next launch,
/// not underneath a running shell.
/// </summary>
/// <remarks>
/// <para>
/// Auto-update is on the list of things <c>CLAUDE.md</c> says to push back on, alongside
/// telemetry and crash reporting, and it was added deliberately rather than by drift. The reason
/// the rule exists is maintenance burden, so this is kept to the smallest thing that works: one
/// source, no channels, no background timer, no silent restart. It checks once at launch and
/// whenever the About screen asks.
/// </para>
/// <para>
/// It is also the only part of Muster that talks to a server on its own initiative. It sends
/// nothing: a release feed request carries no identity and no configuration, and there is no
/// telemetry attached to it. Turning it off entirely is a matter of not pressing the button and
/// setting <see cref="CheckOnLaunch"/> false.
/// </para>
/// <para>
/// Nothing here works in a development build, and that is not a failure to report loudly.
/// <see cref="UpdateManager.IsInstalled"/> is false when running out of <c>bin</c>, so the About
/// screen says so plainly instead of showing an error the developer cannot act on.
/// </para>
/// </remarks>
public sealed class UpdateService
{
    private readonly ILogger<UpdateService> _log;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _staged;

    public UpdateService(ILogger<UpdateService> log)
    {
        _log = log;

        try
        {
            // Public repo, so no token. Prereleases are excluded: a release has to be promoted
            // out of draft before it reaches anyone.
            _manager = new UpdateManager(new GithubSource(ProjectUrl, null, false));
        }
        catch (Exception ex)
        {
            // A malformed feed URL or a locator that cannot find the install should cost the
            // update feature, never the shell.
            _log.LogWarning(ex, "Updates are unavailable");
            _manager = null;
        }
    }

    public const string ProjectUrl = "https://github.com/jamtheradar/Muster";

    /// <summary>Whether to look at launch. The manual button ignores this.</summary>
    public bool CheckOnLaunch { get; init; } = true;

    /// <summary>
    /// False when running from a build folder rather than an install, which is the normal state
    /// during development and is reported as such rather than as an error.
    /// </summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>The version Velopack believes is running, or null outside an install.</summary>
    public string? InstalledVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>True once an update is downloaded and waiting for the next launch.</summary>
    public bool IsPending => _staged is not null || _manager?.UpdatePendingRestart is not null;

    /// <summary>
    /// Looks for a newer release and downloads it if there is one. The returned string is meant
    /// to be shown to a person; failures come back as a sentence rather than an exception,
    /// because a release feed being unreachable is an ordinary Tuesday and not a fault.
    /// </summary>
    public async Task<string> CheckAsync(CancellationToken ct = default)
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            return "Running from a build folder, so there is nothing to update. " +
                   "Updates apply to the installed copy.";
        }

        try
        {
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is null)
            {
                _log.LogInformation("No update available; {Version} is current", InstalledVersion);
                return $"Muster {InstalledVersion} is up to date.";
            }

            var version = update.TargetFullRelease.Version.ToString();
            _log.LogInformation("Update {Version} found, downloading", version);

            await _manager.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);
            _staged = update;

            _log.LogInformation("Update {Version} downloaded and waiting for restart", version);
            return $"Muster {version} is downloaded. It will be applied next time you start.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not check for updates");
            return $"Could not reach the release feed: {ex.Message}";
        }
    }

    /// <summary>
    /// Applies a staged update now, which closes Muster and starts the new one. Returns false if
    /// there is nothing staged, in which case nothing happens.
    /// </summary>
    public bool ApplyAndRestart()
    {
        if (_manager is null || _staged is null)
        {
            return false;
        }

        _log.LogInformation("Applying update and restarting");
        _manager.ApplyUpdatesAndRestart(_staged.TargetFullRelease);
        return true;
    }
}
