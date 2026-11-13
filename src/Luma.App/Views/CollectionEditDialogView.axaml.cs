using Avalonia.Controls;
using Avalonia.Threading;

namespace Luma.App.Views;

public partial class CollectionEditDialogView : UserControl
{
    public CollectionEditDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { this.FindControl<TextBox>("NameBox")?.Focus(); }, DispatcherPriority.Loaded);
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
