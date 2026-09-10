using Muster.Core.Config;

namespace Muster.Core.Presence;

/// <summary>
/// One Teams identity that presence can be written for, flattened out of the config tree so
/// nothing downstream has to walk workspaces to find them. See SPEC section 8.3.
/// </summary>
/// <param name="ServiceId">
/// The service this account belongs to, which is also its session id. That equality is what lets
/// the state machine's "everyone except the sessions in the call" answer be applied directly.
/// </param>
public sealed record PresenceAccount(
    string ServiceId,
    string WorkspaceId,
    string WorkspaceName,
    PresenceStrategyKind Strategy,
    string UserPrincipalName,
    string TenantId)
{
    /// <summary>How the account is named in logs, the settings list and the indicator tooltip.</summary>
    public string Label => string.IsNullOrEmpty(UserPrincipalName)
        ? WorkspaceName
        : $"{WorkspaceName} · {UserPrincipalName}";

    /// <summary>
    /// Every Teams service with a presence strategy other than <see cref="PresenceStrategyKind.None"/>.
    /// Services without a presence block, and generic services, are not accounts at all.
    /// </summary>
    public static IReadOnlyList<PresenceAccount> From(MusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Workspaces
            .SelectMany(workspace => workspace.Services.Select(service => (workspace, service)))
            .Where(pair => pair.service.Kind == ServiceKind.Teams
                && pair.service.Presence is { Strategy: not PresenceStrategyKind.None })
            .Select(pair => new PresenceAccount(
                ServiceId: pair.service.Id,
                WorkspaceId: pair.workspace.Id,
                WorkspaceName: pair.workspace.Name,
                Strategy: pair.service.Presence!.Strategy,
                UserPrincipalName: pair.service.Presence.UserPrincipalName ?? string.Empty,
                TenantId: pair.service.Presence.TenantId ?? string.Empty))
            .ToList();
    }
}
