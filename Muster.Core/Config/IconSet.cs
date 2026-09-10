namespace Muster.Core.Config;

/// <summary>
/// Which of the shipped marks the shell wears: the tray icon, the window icons and the toast
/// sender. Same artwork in every case, in a different colourway.
/// </summary>
/// <remarks>
/// A closed set rather than a path, so a config cannot point the tray at an arbitrary file and so
/// the settings screen can show every choice without scanning a folder. Each value names an
/// <c>.ico</c> copied beside the exe; adding one means adding artwork and a member together.
/// </remarks>
public enum IconSet
{
    /// <summary>Bone ring and ochre core on the slate ground. The original, and the default.</summary>
    Slate,

    /// <summary>The same mark on a eucalypt green ground.</summary>
    Eucalypt,

    /// <summary>Slate ring on a bone ground. Reads better on a dark taskbar.</summary>
    Inverted,

    /// <summary>Ochre ground, slate ring, bone core.</summary>
    Ochre,

    /// <summary>Bone ground, ochre ring, slate core.</summary>
    Bone,

    /// <summary>The default with a bone rim, so the tile keeps an edge on a dark taskbar.</summary>
    Rimmed,

    /// <summary>Slate on safety yellow. Written <c>hiVis</c> in the file.</summary>
    HiVis,

    /// <summary>The same mark on a red-brown clay ground.</summary>
    Clay,
}
