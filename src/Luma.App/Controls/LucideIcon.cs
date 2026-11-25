using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Luma.App.Controls;

/// <summary>
/// Draws a Lucide-style 24x24 stroke icon (SVG path data, see Styles/Icons.xaml) in the inherited
/// <see cref="Control.Foreground"/>, so icons inside buttons follow the button's text colour and hover states.
/// Unlike <c>PathIcon</c> it strokes the outline (round caps/joins) and only fills when <see cref="IsFilled"/> is set.
/// </summary>
public sealed partial class LucideIcon : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(string), typeof(LucideIcon), new PropertyMetadata(null, (d, _) => ((LucideIcon)d).UpdateGeometry()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LucideIcon), new PropertyMetadata(18d, (d, _) => ((LucideIcon)d).UpdateSize()));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LucideIcon), new PropertyMetadata(1.75d, (d, _) => ((LucideIcon)d).UpdateStroke()));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(LucideIcon), new PropertyMetadata(false, (d, _) => ((LucideIcon)d).UpdateFill()));

    private readonly Viewbox _viewbox;
    private readonly Path _path;

    public LucideIcon()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _path = new Path
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        // Stroke follows this control's (inherited) Foreground.
        _path.SetBinding(Shape.StrokeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        _viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = _path };
        Content = _viewbox;
        UpdateSize();
        UpdateStroke();
        UpdateFill();
    }

    /// <summary>SVG path data on a 24x24 grid, e.g. <c>{StaticResource Icon.Heart}</c>.</summary>
    public string? Data { get => (string?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>Rendered width and height in pixels (default 18).</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>Stroke width in the icon's 24-unit grid (default 1.75).</summary>
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    /// <summary>Fills the shape with the foreground as well as stroking it (used for the active favourite heart).</summary>
    public bool IsFilled { get => (bool)GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }

    private void UpdateSize()
    {
        _viewbox.Width = Size;
        _viewbox.Height = Size;
    }

    // Thickness is expressed in the 24-unit design grid (Lucide default is 2), then scaled with the icon.
    private void UpdateStroke() => _path.StrokeThickness = StrokeThickness * 24d / 21d;

    private void UpdateFill()
    {
        if (IsFilled) _path.SetBinding(Shape.FillProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        else _path.ClearValue(Shape.FillProperty);
    }

    // A Geometry instance can only be used by one element in WinUI, so every icon parses its own copy.
    private void UpdateGeometry() => _path.Data = Parse(Data);

    private static readonly Regex Tokens = new(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// Parses SVG path data into a <see cref="Geometry"/>. Lucide data uses SVG shorthands (".5.5", "1.5-2.9") that the XAML
    /// path mini-language is stricter about, so numbers are re-joined with explicit separators first.
    /// </summary>
    internal static Geometry? Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        var normalized = Normalize(data);
        try
        {
            return (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), normalized);
        }
        catch (Exception)
        {
            try
            {
                var xaml = $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{normalized}\" />";
                return ((Path)XamlReader.Load(xaml)).Data;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    internal static string Normalize(string data)
    {
        var builder = new StringBuilder(data.Length + 16);
        foreach (Match match in Tokens.Matches(data))
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(match.Value);
        }
        return builder.ToString();
    }
}
