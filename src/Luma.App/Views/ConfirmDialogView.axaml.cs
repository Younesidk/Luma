using Avalonia.Controls;
using Avalonia.Threading;

namespace Luma.App.Views;

public partial class ConfirmDialogView : UserControl
{
    public ConfirmDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { Button? target = this.FindControl<Button>(((Services.ConfirmDialogViewModel?)DataContext)?.IsDestructive == true ? "CancelButton" : "ConfirmButton"); target?.Focus(); }, DispatcherPriority.Loaded);
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
