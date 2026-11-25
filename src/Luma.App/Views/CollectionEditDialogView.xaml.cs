using Luma.App.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Luma.App.Views;

/// <summary>Create or rename a collection. Opens with the name field focused; Enter confirms.</summary>
public sealed partial class CollectionEditDialogView : UserControl
{
    internal CollectionEditDialogView(CollectionEditDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewHelpers.FocusWhenLoaded(NameBox, this);
    }

    internal CollectionEditDialogViewModel ViewModel { get; }

    private void OnNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ViewModel.ConfirmCommand.CanExecute(null)) ViewModel.ConfirmCommand.Execute(null);
    }
}
