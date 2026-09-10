using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Muster.Core.Config;

namespace Muster.App.Configuration;

/// <summary>
/// The app's own mark, in whichever colourway the config asks for, and the one place that knows
/// where those files are. Everything that draws the icon reads through here and listens for
/// <see cref="Changed"/>.
/// </summary>
/// <remarks>
/// <para>
/// The icon reaches four surfaces and only three of them can be repainted: the tray icon, the
/// window icons and the toast sender all follow this, while the executable's own icon is baked in
/// by <c>ApplicationIcon</c> at compile time. So Explorer, the pinned taskbar shortcut and the
/// Alt+Tab entry for a window that has not been given an icon keep showing the default. That is
/// worth saying in the settings screen rather than leaving the user to notice it.
/// </para>
/// <para>
/// A missing file falls back to <see cref="IconSet.Slate"/> rather than throwing. The set is
/// closed and every member is copied by the build, so a gap means a broken deployment — which is
/// a thing to log and carry on from, not a reason for the shell to fail to start.
/// </para>
/// </remarks>
public sealed class AppIcons(ILogger<AppIcons> log)
{
    private const IconSet Fallback = IconSet.Slate;

    /// <summary>Big enough to judge a colourway by, and a real frame in every one of the files.</summary>
    private const int PreviewSize = 48;

    /// <summary>Decoded frames, keyed by set and requested size. Frozen, so any thread may use them.</summary>
    private readonly Dictionary<(IconSet Set, int Size), ImageSource?> _decoded = [];

    /// <summary>
    /// The words that tell the sets apart, in the order the settings screen offers them. Display
    /// text belongs here rather than on the enum: <c>Muster.Core</c> describes the config, not the
    /// UI that edits it.
    /// </summary>
    private static readonly (IconSet Value, string Name, string Description)[] Catalogue =
    [
        (IconSet.Slate, "Slate", "Bone ring and ochre core on slate. The original, and the default."),
        (IconSet.Rimmed, "Rimmed", "The default with a bone rim, so the tile keeps an edge against a dark taskbar."),
        (IconSet.Inverted, "Inverted", "Slate on bone. The one that stays legible against a light taskbar."),
        (IconSet.Bone, "Bone", "Bone ground, ochre ring, slate core. The lightest of the set."),
        (IconSet.Ochre, "Ochre", "Ochre ground, with the ring in slate and the core in bone."),
        (IconSet.Eucalypt, "Eucalypt", "The same mark on a eucalypt green ground."),
        (IconSet.Clay, "Clay", "A warmer red-brown ground, bone ring, ochre core."),
        (IconSet.HiVis, "Hi-vis", "Slate on safety yellow. The hardest one to lose in a crowded tray."),
    ];

    private IReadOnlyList<IconSetOption>? _options;

    /// <summary>
    /// Every set with a picture of it, for the settings picker. A colourway is not something a
    /// name conveys, so the choice is offered as the artwork itself.
    /// </summary>
    public IReadOnlyList<IconSetOption> Options => _options ??= Catalogue
        .Select(entry => new IconSetOption(entry.Value, entry.Name, entry.Description, Image(entry.Value, PreviewSize)))
        .ToList();

    /// <summary>The set in force right now. Starts on the default until a config is applied.</summary>
    public IconSet Current { get; private set; } = Fallback;

    /// <summary>Raised on the dispatcher, because <see cref="LiveSettings"/> is only called there.</summary>
    public event EventHandler? Changed;

    /// <summary>The <c>.ico</c> beside the exe for <paramref name="set"/>, whether or not it exists.</summary>
    public static string PathOf(IconSet set) => Path.Combine(AppContext.BaseDirectory, "Assets", FileNameOf(set));

    /// <summary>The file the current set actually resolves to, having fallen back if it is missing.</summary>
    public string CurrentPath => PathOf(Resolve(Current));

    /// <summary>
    /// Adopts the appearance section. Safe to call with an unchanged config: nothing is raised
    /// unless the choice really moved, so redrawing the tray is not on the path of every reload.
    /// </summary>
    public void Apply(AppearanceConfig appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        if (appearance.IconSet == Current)
        {
            return;
        }

        Current = appearance.IconSet;
        log.LogInformation("Icon set is now {IconSet} ({Path})", Current, CurrentPath);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A frozen frame of <paramref name="set"/> at or near <paramref name="size"/> pixels, for a
    /// window icon or a settings preview. Null if the file cannot be read at all.
    /// </summary>
    /// <remarks>
    /// The frame is chosen out of the <c>.ico</c> by hand rather than left to the decoder's
    /// default, which is the first frame in the file and so depends on the order the artwork was
    /// written in.
    /// </remarks>
    public ImageSource? Image(IconSet set, int size)
    {
        var resolved = Resolve(set);

        if (_decoded.TryGetValue((resolved, size), out var cached))
        {
            return cached;
        }

        var image = Decode(PathOf(resolved), size);
        _decoded[(resolved, size)] = image;
        return image;
    }

    /// <summary>The current set at the size a title bar and taskbar button want.</summary>
    public ImageSource? CurrentWindowIcon => Image(Current, 32);

    private static string FileNameOf(IconSet set) => set switch
    {
        IconSet.Eucalypt => "muster-eucalypt.ico",
        IconSet.Inverted => "muster-inverted.ico",
        IconSet.Ochre => "muster-ochre.ico",
        IconSet.Bone => "muster-bone.ico",
        IconSet.Rimmed => "muster-rimmed.ico",
        IconSet.HiVis => "muster-hivis.ico",
        IconSet.Clay => "muster-clay.ico",
        _ => "muster.ico",
    };

    /// <summary>Substitutes the default for a set whose artwork is not beside the exe.</summary>
    private IconSet Resolve(IconSet set)
    {
        if (File.Exists(PathOf(set)))
        {
            return set;
        }

        log.LogWarning("Icon set {IconSet} is missing from {Path}, using {Fallback}", set, PathOf(set), Fallback);
        return Fallback;
    }

    private ImageSource? Decode(string path, int size)
    {
        try
        {
            using var stream = File.OpenRead(path);

            // OnLoad so the stream can close underneath it.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            var frame = decoder.Frames
                .OrderBy(candidate => Math.Abs(candidate.PixelWidth - size))
                .ThenByDescending(candidate => candidate.PixelWidth)
                .FirstOrDefault();

            frame?.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the icon {Path}", path);
            return null;
        }
    }
}
