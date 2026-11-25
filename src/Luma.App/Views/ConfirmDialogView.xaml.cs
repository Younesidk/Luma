using Luma.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>Yes/no confirmation; destructive confirmations use the danger button and start focused on Cancel.</summary>
public sealed partial class ConfirmDialogView : UserControl
{
    internal ConfirmDialogView(ConfirmDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewHelpers.FocusWhenLoaded(viewModel.IsDestructive ? CancelButton : ConfirmButton, this);
    }

    internal ConfirmDialogViewModel ViewModel { get; }
}
