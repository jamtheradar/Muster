using System.Windows.Media;
using Muster.Core.Config;

namespace Muster.App.Configuration;

/// <summary>
/// One icon set as the settings screen offers it: the config value, the words that tell it apart
/// from the others, and a picture of it. See <see cref="AppIcons.Options"/>.
/// </summary>
public sealed record IconSetOption(IconSet Value, string Name, string Description, ImageSource? Preview);
