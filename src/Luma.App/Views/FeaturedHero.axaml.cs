using Avalonia;
using Avalonia.Controls;

namespace Luma.App.Views;

public partial class FeaturedHero : UserControl
{
    private TopLevel? _top;

    public FeaturedHero()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    /// <summary>On a short window a fixed 300px hero filled the whole gallery viewport; scale it with the window height.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is null) return;
        _top.SizeChanged += OnTopLevelSizeChanged;
        ApplyHeight(_top.Bounds.Height);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null) _top.SizeChanged -= OnTopLevelSizeChanged;
        _top = null;
    }

    private void OnTopLevelSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyHeight(e.NewSize.Height);

    private void ApplyHeight(double windowHeight)
    {
        if (windowHeight <= 0) return;
        var card = this.FindControl<Border>("Card");
        if (card is null) return;
        var height = Math.Round(Math.Clamp((windowHeight - 140) * 0.42, 220, 300));
        if (Math.Abs(card.Height - height) > 0.5) card.Height = height;
    }
}
