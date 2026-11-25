using Luma.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>History: every applied wallpaper, newest first, with Apply again / Show in folder.</summary>
public sealed partial class HistoryView : UserControl
{
    private HistoryViewModel? _viewModel;

    public HistoryView()
    {
        InitializeComponent();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal HistoryViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(value, _viewModel)) return;
            _viewModel = value;
            Bindings.Update();
        }
    }
}
