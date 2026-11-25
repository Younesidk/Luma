using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>One downloaded wallpaper in the Downloads library (DataContext = <see cref="LibraryItemViewModel"/>).</summary>
public sealed partial class LibraryRow : UserControl
{
    public LibraryRow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Internal: the row view model exposes Core models (see WallpaperCard.ViewModel).</summary>
    internal LibraryItemViewModel? ViewModel { get; private set; }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = args.NewValue as LibraryItemViewModel;
        if (ReferenceEquals(vm, ViewModel)) return;
        ViewModel = vm;
        Bindings.Update();
    }
}
