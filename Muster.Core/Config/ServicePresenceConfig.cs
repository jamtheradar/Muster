namespace Muster.Core.Config;

/// <summary>Per-account presence settings for a Teams service.</summary>
public sealed record ServicePresenceConfig
{
    public PresenceStrategyKind Strategy { get; init; } = PresenceStrategyKind.None;

    /// <summary>The signed-in account, used as the Graph <c>users/{id}</c> segment.</summary>
    public string? UserPrincipalName { get; init; }

    public string? TenantId { get; init; }
}
