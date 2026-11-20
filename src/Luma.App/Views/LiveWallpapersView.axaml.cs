using Avalonia.Controls;
using Avalonia.Input;
using Luma.App.ViewModels;

namespace Luma.App.Views;

public partial class LiveWallpapersView : UserControl
{
    public LiveWallpapersView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is LiveWallpapersViewModel { IsPreviewOpen: true } vm)
            {
                vm.ClosePreviewCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
