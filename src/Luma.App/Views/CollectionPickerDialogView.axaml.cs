using Avalonia.Controls;
using Avalonia.Threading;

namespace Luma.App.Views;

public partial class CollectionPickerDialogView : UserControl
{
    public CollectionPickerDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { this.FindControl<TextBox>("NewNameBox")?.Focus(); }, DispatcherPriority.Loaded);
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
