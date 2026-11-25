using System.ComponentModel;
using Luma.App.Controls;
using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>
/// Live wallpapers: import, the "now on your desktop" banner, the video gallery and a dark full-page preview that plays
/// the video in a <see cref="VideoPreviewHost"/> with display and fit selectors. Esc closes the preview.
/// </summary>
public sealed partial class LiveWallpapersView : UserControl, IEscapeHandler
{
    private LiveWallpapersViewModel? _viewModel;
    private readonly List<Action> _detach = [];
    private long _errorToken;

    public LiveWallpapersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal LiveWallpapersViewModel? ViewModel
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

    public bool HandleEscape()
    {
        if (_viewModel is not { IsPreviewOpen: true } vm) return false;
        vm.ClosePreviewCommand.Execute(null);
        return true;
    }

    private void Attach()
    {
        if (_detach.Count > 0 || _viewModel is not { } vm) return;
        vm.PropertyChanged += OnViewModelChanged;
        _detach.Add(() => vm.PropertyChanged -= OnViewModelChanged);

        // The host reports playback errors through its Error property; mirror them into the view model.
        _errorToken = PreviewHost.RegisterPropertyChangedCallback(VideoPreviewHost.ErrorProperty, OnPreviewHostError);
        _detach.Add(() => PreviewHost.UnregisterPropertyChangedCallback(VideoPreviewHost.ErrorProperty, _errorToken));

        _detach.Add(ViewHelpers.BindComboLive(DisplayCombo, vm.DisplayOptions, o => o.Label, () => vm.SelectedDisplay!, v => vm.SelectedDisplay = v,
            vm, nameof(LiveWallpapersViewModel.SelectedDisplay)));
        _detach.Add(ViewHelpers.BindCombo(FitCombo, vm.FitOptions, o => o.Label, () => vm.SelectedFit, v => vm.SelectedFit = v,
            vm, nameof(LiveWallpapersViewModel.SelectedFit)));
        UpdatePreview();
    }

    private void Detach()
    {
        foreach (var detach in _detach) detach();
        _detach.Clear();
        // Never keep a decoder (and a file handle) alive for a page nobody sees.
        PreviewHost.Source = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveWallpapersViewModel.PreviewItem) or null or "") UpdatePreview();
    }

    /// <summary>
    /// The preview item is applied in code: <see cref="VideoItemViewModel"/> exposes a Core model, so it stays out of
    /// compiled-binding paths.
    /// </summary>
    private void UpdatePreview()
    {
        var item = _viewModel?.PreviewItem;
        PreviewTitle.Text = item?.Title ?? string.Empty;
        PreviewMeta.Text = item?.MetaText ?? string.Empty;
        if (!string.Equals(PreviewHost.Source, item?.FilePath, StringComparison.Ordinal)) PreviewHost.Source = item?.FilePath;
        if (item is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ClosePreviewButton.Focus(FocusState.Programmatic));
        }
    }

    private void OnPreviewHostError(DependencyObject sender, DependencyProperty property)
    {
        if (_viewModel is not { IsPreviewOpen: true } vm) return;
        var error = PreviewHost.Error;
        if (!string.IsNullOrEmpty(error) && vm.PreviewError != error) vm.PreviewError = error;
    }
}
