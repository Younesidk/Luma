using Avalonia.Controls;

namespace Luma.App.Views;

public partial class FilterPanel : UserControl
{
    public FilterPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
