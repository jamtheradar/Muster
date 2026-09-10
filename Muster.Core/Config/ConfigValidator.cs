using System.Text.RegularExpressions;
using Muster.Core.Services;

namespace Muster.Core.Config;

/// <summary>
/// Checks a loaded config for the mistakes that are easy to make by hand and expensive to debug
/// at runtime: duplicate ids, ids that are not safe as folder names, and bad URLs.
/// </summary>
public static partial class ConfigValidator
{
    /// <summary>Longest workspace abbreviation the rail tile can show without shrinking to noise.</summary>
    public const int MaxAbbreviationLength = 3;

    /// <summary>The icon set names as they are spelled in the file, for the message above.</summary>
    private static readonly string KnownIconSets = string.Join(
        ", ",
        Enum.GetNames<IconSet>().Select(name => $"'{name.ToLowerInvariant()}'"));

    /// <summary>Returns every problem found. An empty list means the config is usable.</summary>
    public static IReadOnlyList<string> Validate(MusterConfig config)
    {
        var problems = new List<string>();

        if (config.Version > MusterConfig.CurrentVersion)
        {
            problems.Add(
                $"version {config.Version} is newer than this build understands " +
                $"(max {MusterConfig.CurrentVersion}).");
        }

        var workspaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in config.Workspaces)
        {
            var where = $"workspace '{workspace.Id}'";

            if (!IsSafeId(workspace.Id))
            {
                problems.Add($"{where}: id must be letters, digits, dash or underscore, and start with a letter or digit.");
            }
            else if (!workspaceIds.Add(workspace.Id))
            {
                problems.Add($"{where}: duplicate workspace id.");
            }

            if (string.IsNullOrWhiteSpace(workspace.Name))
            {
                problems.Add($"{where}: name is required.");
            }

            // Three characters is what the 34px rail tile holds at a legible size.
            if (workspace.Abbreviation is { } abbreviation
                && (string.IsNullOrWhiteSpace(abbreviation) || abbreviation.Trim().Length > MaxAbbreviationLength))
            {
                problems.Add(
                    $"{where}: abbreviation must be 1 to {MaxAbbreviationLength} characters, " +
                    "or omitted to use initials from the name.");
            }

            if (!HexColour().IsMatch(workspace.Accent))
            {
                problems.Add($"{where}: accent '{workspace.Accent}' is not a #RRGGBB colour.");
            }

            if (workspace.ProfileName is { } profile && !IsSafeId(profile))
            {
                problems.Add($"{where}: profileName '{profile}' is not a valid profile name.");
            }

            foreach (var service in workspace.Services)
            {
                problems.AddRange(ValidateService(workspace, service, serviceIds));
            }
        }

        if (config.Presence.CallDetectDelaySeconds < 0 || config.Presence.CallClearDelaySeconds < 0)
        {
            problems.Add("presence: call detect and clear delays cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(config.Presence.ExpirationDuration))
        {
            problems.Add(
                "presence: expirationDuration is required. Without it a crash mid-call leaves " +
                "Busy stuck across every tenant.");
        }

        problems.AddRange(ValidateGraphRegistration(config));

        if (config.Notifications.HistoryLimit is < 1 or > 5000)
        {
            problems.Add("notifications: historyLimit must be between 1 and 5000.");
        }

        // Rejected rather than defaulted, for the same reason the dom presence strategy is: a
        // setting that silently does nothing is worse than one that does not load, and where a
        // link ends up is only visible after you have already clicked it.
        if (!Enum.IsDefined(config.Links.External))
        {
            problems.Add(
                $"links: external '{config.Links.External}' is not a known policy. " +
                "Use 'never', 'auto' or 'always'.");
        }

        // The icon set is cosmetic, so an unknown one could be defaulted away. It is not, because
        // the file is hand-edited: a typo that silently keeps the old mark reads as the setting
        // being ignored, and the next thing tried is editing it again.
        if (!Enum.IsDefined(config.Appearance.IconSet))
        {
            problems.Add(
                $"appearance: iconSet '{config.Appearance.IconSet}' is not a known icon set. " +
                $"Use one of: {KnownIconSets}.");
        }

        // Zero would suspend a workspace the instant it went off screen, which is indistinguishable
        // from the app breaking. Turning suspension off is what enabled:false is for.
        if (config.Suspension.IdleMinutes is < 1 or > SuspensionConfig.MaxIdleMinutes)
        {
            problems.Add(
                $"suspension: idleMinutes must be between 1 and {SuspensionConfig.MaxIdleMinutes}. " +
                "Set enabled to false to stop suspending altogether.");
        }

        problems.AddRange(ValidateLogging(config.Logging));

        return problems;
    }

    /// <summary>
    /// The app registration the Graph strategy signs in with. Only insisted on once presence is
    /// actually switched on: a half-configured account with <c>presence.enabled</c> false is a
    /// normal state to leave the file in while waiting on a tenant admin to consent.
    /// </summary>
    private static IEnumerable<string> ValidateGraphRegistration(MusterConfig config)
    {
        var clientId = config.Presence.ClientId;

        if (!string.IsNullOrWhiteSpace(clientId) && !Guid.TryParse(clientId, out _))
        {
            yield return $"presence: clientId '{clientId}' is not a GUID. It is the application (client) id of the app registration.";
        }

        if (!config.Presence.Enabled || !string.IsNullOrWhiteSpace(clientId))
        {
            yield break;
        }

        var graphAccounts = config.Workspaces
            .SelectMany(workspace => workspace.Services)
            .Count(service => service.Presence?.Strategy == PresenceStrategyKind.Graph);

        if (graphAccounts > 0)
        {
            yield return
                "presence: clientId is required once presence is enabled and an account uses the " +
                "graph strategy. It is the application (client) id of the multi-tenant app " +
                "registration with delegated Presence.ReadWrite.";
        }
    }

    private static IEnumerable<string> ValidateLogging(LoggingConfig logging)
    {
        if (!Enum.IsDefined(logging.Level))
        {
            yield return $"logging: level '{logging.Level}' is not a known level.";
        }

        if (logging.RetentionDays is < 0 or > LoggingConfig.MaxRetentionDays)
        {
            yield return
                $"logging: retentionDays must be between 0 and {LoggingConfig.MaxRetentionDays} " +
                "(0 keeps every file forever).";
        }

        if (logging.Directory is not { } directory)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            yield return "logging: directory must be a path, or omitted for the default folder.";
            yield break;
        }

        // A relative path resolves against the working directory, which for a shortcut launch is
        // not anywhere the user would think to look for their logs.
        var qualified = false;
        try
        {
            qualified = Path.IsPathFullyQualified(directory);
        }
        catch (ArgumentException)
        {
            // Invalid characters. Reported below as not fully qualified, which it is not.
        }

        if (!qualified)
        {
            yield return $"logging: directory '{directory}' must be an absolute path.";
        }
    }

    private static IEnumerable<string> ValidateService(
        WorkspaceConfig workspace,
        ServiceConfig service,
        HashSet<string> serviceIds)
    {
        var where = $"service '{service.Id}' in workspace '{workspace.Id}'";

        if (!IsSafeId(service.Id))
        {
            yield return $"{where}: id must be letters, digits, dash or underscore, and start with a letter or digit.";
        }
        else if (!serviceIds.Add(service.Id))
        {
            yield return $"{where}: duplicate service id. Service ids must be unique across all workspaces.";
        }

        if (string.IsNullOrWhiteSpace(service.Name))
        {
            yield return $"{where}: name is required.";
        }

        if (!UrlNormaliser.IsSupportedScheme(service.Url))
        {
            yield return $"{where}: url must be an absolute http or https URL, got '{service.Url}'.";
        }

        if (service.ProfileName is { } profile && !IsSafeId(profile))
        {
            yield return $"{where}: profileName '{profile}' is not a valid profile name.";
        }

        if (service.Presence is not { } presence)
        {
            yield break;
        }

        if (service.Kind != ServiceKind.Teams && presence.Strategy != PresenceStrategyKind.None)
        {
            yield return $"{where}: presence applies to teams services only.";
        }

        // Reserved in the schema, not built. Reported rather than ignored: a config asking for a
        // strategy that silently does nothing is the worst of both, because presence failing to
        // apply is invisible by nature.
        if (presence.Strategy == PresenceStrategyKind.Dom)
        {
            yield return
                $"{where}: presence strategy 'dom' is not implemented. Use 'graph', or 'none' for " +
                "a tenant that will not consent.";
        }

        // The page strategy needs neither of the two below: it acts as whichever account the tab
        // is signed in as, and learns that identity by watching the tab's own traffic. Demanding
        // a UPN and tenant it will never read would only be something to get out of step.
        if (presence.Strategy == PresenceStrategyKind.Graph)
        {
            if (string.IsNullOrWhiteSpace(presence.UserPrincipalName))
            {
                yield return $"{where}: presence.userPrincipalName is required for the graph strategy.";
            }

            if (string.IsNullOrWhiteSpace(presence.TenantId))
            {
                yield return $"{where}: presence.tenantId is required for the graph strategy.";
            }
        }
    }

    // Ids become WebView2 profile names and appear in folder paths, so keep them boring.
    private static bool IsSafeId(string id) => !string.IsNullOrEmpty(id) && SafeId().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex SafeId();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}
