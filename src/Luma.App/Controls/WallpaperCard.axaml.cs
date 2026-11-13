using Avalonia.Controls;

namespace Luma.App.Controls;

public partial class WallpaperCard : UserControl
{
    public WallpaperCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
