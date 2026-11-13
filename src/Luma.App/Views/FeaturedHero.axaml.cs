using Avalonia.Controls;

namespace Luma.App.Views;

public partial class FeaturedHero : UserControl
{
    public FeaturedHero()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
