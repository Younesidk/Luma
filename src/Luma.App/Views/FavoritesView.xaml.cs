using Luma.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>Favorites: header with count, search / source / sort bar and the favourites gallery.</summary>
public sealed partial class FavoritesView : UserControl, ISearchFocusable
{
    private FavoritesViewModel? _viewModel;
    private readonly List<Action> _detach = [];

    public FavoritesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal FavoritesViewModel? ViewModel
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
        if (_detach.Count > 0 || _viewModel is not { } vm) return;
        _detach.Add(ViewHelpers.BindCombo(ProviderCombo, vm.ProviderChoices, o => o.Label, () => vm.SelectedProvider, v => vm.SelectedProvider = v,
            vm, nameof(FavoritesViewModel.SelectedProvider)));
        _detach.Add(ViewHelpers.BindCombo(SortCombo, vm.Sorts, o => o.Label, () => vm.SelectedSort, v => vm.SelectedSort = v,
            vm, nameof(FavoritesViewModel.SelectedSort)));
    }

    private void Detach()
    {
        foreach (var detach in _detach) detach();
        _detach.Clear();
    }
}
