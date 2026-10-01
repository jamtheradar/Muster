namespace Muster.Core.Media;

/// <summary>
/// What the microphone is actually doing, as distinct from whether it is open.
/// </summary>
/// <remarks>
/// Ordered deliberately: <see cref="MicSignalMonitor"/> aggregates several sessions by taking the
/// highest value, so one frame picking up sound outranks another sitting muted.
/// </remarks>
public enum MicSignal
{
    /// <summary>Nothing is capturing, so there is nothing to say.</summary>
    Unknown = 0,

    /// <summary>The track is open but switched off — Teams' own mute button.</summary>
    Muted = 1,

    /// <summary>Open and unmuted, but nothing has been picked up recently.</summary>
    Quiet = 2,

    /// <summary>Open, unmuted, and hearing something.</summary>
    Live = 3,
}
