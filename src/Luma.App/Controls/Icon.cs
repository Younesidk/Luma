using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Luma.App.Controls;

/// <summary>
/// Draws a Lucide-style 24x24 stroke icon (a <see cref="Geometry"/>) in the inherited foreground colour, so icons
/// inside buttons automatically follow the button's text colour and hover states.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 18);
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.75);
    public static readonly StyledProperty<bool> IsFilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(IsFilled));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeThicknessProperty, IsFilledProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    /// <summary>Fills the shape as well as stroking it (used for the active favourite heart).</summary>
    public bool IsFilled { get => GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null) return;
        var scale = Size / 24d;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            // Thickness is expressed in the icon's 24-unit design grid (Lucide default: 2).
            var pen = new Pen(Foreground, StrokeThickness * 24d / 21d, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(IsFilled ? Foreground : null, pen, Data);
        }
    }
}
