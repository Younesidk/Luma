using Luma.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>Downloads: import, search / sort, active transfers and the local library list.</summary>
public sealed partial class DownloadsView : UserControl, ISearchFocusable
{
    private DownloadsViewModel? _viewModel;
    private Action? _detachSort;

    public DownloadsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal DownloadsViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(value, _viewModel)) return;
            Detach();
            _viewModel = value;
            Bindings.Update();
            if (IsLoaded) Attach();
        }
    }

    public bool FocusSearch() => ViewHelpers.FocusAndSelect(PageSearch);

    private void Attach()
    {
        if (_detachSort is not null || _viewModel is not { } vm) return;
        _detachSort = ViewHelpers.BindCombo(SortCombo, vm.Sorts, o => o.Label, () => vm.SelectedSort, v => vm.SelectedSort = v,
            vm, nameof(DownloadsViewModel.SelectedSort));
    }

    private void Detach()
    {
        _detachSort?.Invoke();
        _detachSort = null;
    }
}
