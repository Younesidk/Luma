using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>One transfer in the Downloads page (DataContext = <see cref="DownloadJobViewModel"/>).</summary>
public sealed partial class DownloadJobRow : UserControl
{
    public DownloadJobRow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Internal: the row view model exposes Core models (see WallpaperCard.ViewModel).</summary>
    internal DownloadJobViewModel? ViewModel { get; private set; }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = args.NewValue as DownloadJobViewModel;
        if (ReferenceEquals(vm, ViewModel)) return;
        ViewModel = vm;
        Bindings.Update();
    }
}
