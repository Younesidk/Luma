using System.ComponentModel;
using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>
/// Collections: an overview of mosaic cover cards (with context menus) and, once one is opened, its detail view with
/// back / edit / delete and the wallpaper gallery.
/// </summary>
public sealed partial class CollectionsView : UserControl
{
    private CollectionsViewModel? _viewModel;
    private bool _attached;

    public CollectionsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal CollectionsViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(value, _viewModel)) return;
            Detach();
            _viewModel = value;
            Bindings.Update();
            UpdateDetailHeader();
            if (IsLoaded) Attach();
        }
    }

    private void Attach()
    {
        if (_attached || _viewModel is null) return;
        _attached = true;
        _viewModel.PropertyChanged += OnViewModelChanged;
        UpdateDetailHeader();
    }

    private void Detach()
    {
        if (!_attached || _viewModel is null) return;
        _attached = false;
        _viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(CollectionsViewModel.Current) or null or "")) return;
        var opened = UpdateDetailHeader();
        if (opened) DetailGrid.ScrollToStart();
    }

    /// <summary>
    /// The detail header is filled in code: <see cref="CollectionCardViewModel"/> exposes a Core model, so it stays out of
    /// compiled-binding paths. Returns true when a collection is shown.
    /// </summary>
    private bool UpdateDetailHeader()
    {
        var current = _viewModel?.Current;
        DetailName.Text = current?.Name ?? string.Empty;
        DetailDescription.Text = current?.Description ?? string.Empty;
        DetailDescription.Visibility = current?.HasDescription == true ? Visibility.Visible : Visibility.Collapsed;
        DetailCount.Text = current?.CountText ?? string.Empty;
        return current is not null;
    }

    private void OnCollectionPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is CollectionCard card) card.Owner = _viewModel;
    }

    private void OnCollectionClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is CollectionCard card) card.Owner = null;
    }
}
