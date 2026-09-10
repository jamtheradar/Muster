namespace Muster.Core.Presence;

/// <summary>What one pass of <see cref="PresenceApplier.SyncAsync(CancellationToken)"/> did.</summary>
public sealed record PresenceSyncOutcome(int Set, int Cleared, int Renewed, int Failed)
{
    public static readonly PresenceSyncOutcome Nothing = new(0, 0, 0, 0);

    public bool DidAnything => Set > 0 || Cleared > 0 || Renewed > 0 || Failed > 0;

    public override string ToString()
        => $"{Set} set, {Renewed} renewed, {Cleared} cleared, {Failed} failed";
}
