using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Muster.App.Views.Controls;

/// <summary>
/// Picks an <c>#RRGGBB</c> colour without anyone having to know hex: a row of presets, three
/// channel sliders, and the hex box still there for when someone is pasting a brand colour.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a control library, because the only alternative in the box
/// is the WinForms colour dialog, and WinForms is here for the tray icon and nothing else.
/// </remarks>
public partial class ColorPickerField : UserControl
{
    /// <summary>The bound value. Always written back as <c>#RRGGBB</c>, upper case.</summary>
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex),
        typeof(string),
        typeof(ColorPickerField),
        new FrameworkPropertyMetadata(
            "#2D7D9A",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnHexPropertyChanged));

    // One update to Hex fans out to the sliders, the hex box and two swatches, each of which
    // would otherwise write back to Hex. Everything reentrant goes through this gate.
    private bool _updating;

    public ColorPickerField()
    {
        InitializeComponent();
        BuildSwatches();
        Apply(Hex);

        // The toggle is what a screen reader actually lands on, so give it whatever name the
        // field was given. Without this every picker on a screen announces "Choose colour".
        Loaded += (_, _) =>
        {
            if (AutomationProperties.GetName(this) is { Length: > 0 } name)
            {
                AutomationProperties.SetName(Toggle, name);
            }
        };
    }

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    /// <summary>
    /// Accents that stay legible as a 3px strip on a dark chrome and are easy to tell apart at a
    /// glance, which is the whole job of the wrong-tenant guard.
    /// </summary>
    private static readonly string[] Presets =
    [
        "#E5484D", "#F97316", "#B5651D", "#EAB308", "#84CC16", "#22C55E", "#10B981", "#14B8A6",
        "#06B6D4", "#2D7D9A", "#3B82F6", "#6366F1", "#8B5CF6", "#A855F7", "#D946EF", "#EC4899",
        "#F43F5E", "#991B1B", "#9A3412", "#4D7C0F", "#065F46", "#155E75", "#4C1D95", "#78716C",
    ];

    private static void OnHexPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ColorPickerField)d).Apply(e.NewValue as string);

    /// <summary>Pushes the current value out to every part of the control that shows it.</summary>
    private void Apply(string? hex)
    {
        if (_updating || !TryParse(hex, out var colour))
        {
            return;
        }

        _updating = true;
        try
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            SwatchPreview.Background = brush;
            Preview.Background = brush;
            HexLabel.Text = Format(colour);
            HexBox.Text = Format(colour);
            Red.Value = colour.R;
            Green.Value = colour.G;
            Blue.Value = colour.B;
        }
        finally
        {
            _updating = false;
        }
    }

    private void BuildSwatches()
    {
        foreach (var preset in Presets)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset));
            brush.Freeze();

            var swatch = new Button
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(1),
                Background = brush,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5F)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = preset,
                ToolTip = preset,
            };

            AutomationProperties.SetName(swatch, preset);
            swatch.Click += OnSwatchClick;
            Swatches.Children.Add(swatch);
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string preset })
        {
            Hex = preset;
            Toggle.IsChecked = false;
        }
    }

    private void OnChannelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        Hex = Format(Color.FromRgb((byte)Red.Value, (byte)Green.Value, (byte)Blue.Value));
    }

    // The box stays writable while it holds a half-typed value; only a complete #RRGGBB commits.
    private void OnHexBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !TryParse(HexBox.Text, out var colour))
        {
            return;
        }

        Hex = Format(colour);
    }

    private static string Format(Color colour)
        => string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");

    private static bool TryParse(string? hex, out Color colour)
    {
        colour = default;

        if (hex is null)
        {
            return false;
        }

        var text = hex.Trim();
        if (text.Length != 7 || text[0] != '#')
        {
            return false;
        }

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}
