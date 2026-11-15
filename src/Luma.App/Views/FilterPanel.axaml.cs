using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Luma.App.Views;

public partial class FilterPanel : UserControl
{
    public FilterPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The flyout opens below the Filters button; on a short window a fixed 560px panel ran off the bottom of the screen.
    /// Cap it to the space the main window actually has so it scrolls instead.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var scroller = this.FindControl<ScrollViewer>("Scroller");
        if (window is null || scroller is null) return;
        scroller.MaxHeight = Math.Clamp(window.Bounds.Height - 200, 260, 560);
    }
}
