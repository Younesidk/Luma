using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>One applied-history row (DataContext = <see cref="HistoryEntryViewModel"/>).</summary>
public sealed partial class HistoryRow : UserControl
{
    public HistoryRow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Internal: the row view model exposes Core models (see WallpaperCard.ViewModel).</summary>
    internal HistoryEntryViewModel? ViewModel { get; private set; }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = args.NewValue as HistoryEntryViewModel;
        if (ReferenceEquals(vm, ViewModel)) return;
        ViewModel = vm;
        Bindings.Update();
    }
}
