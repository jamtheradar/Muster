namespace Muster.Core.Config;

/// <summary>A new config was read from disk and differs from the one the app is running on.</summary>
/// <param name="Previous">What the app was running on until now.</param>
/// <param name="Current">What it should be running on.</param>
/// <param name="Diff">What has to happen to get from one to the other.</param>
public sealed record ConfigReloadedEventArgs(
    MusterConfig Previous,
    MusterConfig Current,
    ConfigDiff Diff);
