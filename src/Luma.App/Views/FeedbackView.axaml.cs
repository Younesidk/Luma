using Avalonia.Controls;

namespace Luma.App.Views;

public partial class FeedbackView : UserControl
{
    public FeedbackView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
