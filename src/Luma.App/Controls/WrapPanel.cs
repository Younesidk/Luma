using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Luma.App.Controls;

/// <summary>
/// Minimal left-to-right wrapping panel (WinUI has none built in). Children keep their desired size; spacing between
/// them comes from their margins.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (lineWidth > 0 && lineWidth + size.Width > availableSize.Width)
            {
                width = Math.Max(width, lineWidth);
                height += lineHeight;
                lineWidth = lineHeight = 0;
            }
            lineWidth += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(Math.Max(width, lineWidth), height + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return finalSize;
    }
}
