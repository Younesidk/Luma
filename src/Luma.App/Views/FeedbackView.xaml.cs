using Luma.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>Feedback form: type, title, description with a live character counter, error text and submit.</summary>
public sealed partial class FeedbackView : UserControl
{
    private FeedbackViewModel? _viewModel;
    private Action? _detachType;

    public FeedbackView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal FeedbackViewModel? ViewModel
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

    private void Attach()
    {
        if (_detachType is not null || _viewModel is not { } vm) return;
        _detachType = ViewHelpers.BindCombo(TypeCombo, vm.TypeOptions, o => o.Label, () => vm.SelectedType, v => vm.SelectedType = v,
            vm, nameof(FeedbackViewModel.SelectedType));
    }

    private void Detach()
    {
        _detachType?.Invoke();
        _detachType = null;
    }
}
