using System.Collections.Specialized;
using System.ComponentModel;
using Luma.App.Controls;
using Luma.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Luma.App.Views;

/// <summary>
/// "Add to collection": one row per collection (click toggles membership immediately) plus a quick "create" field,
/// which is focused when the dialog opens.
/// </summary>
public sealed partial class CollectionPickerDialogView : UserControl
{
    private readonly List<(CollectionChoiceViewModel Choice, Border Check, LucideIcon Icon)> _rows = [];

    internal CollectionPickerDialogView(CollectionPickerDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        WallpaperTitle.Text = viewModel.Wallpaper.DisplayTitle;
        ViewHelpers.FocusWhenLoaded(NewNameBox, this);

        RebuildRows();
        viewModel.Choices.CollectionChanged += OnChoicesChanged;
        Unloaded += (_, _) =>
        {
            viewModel.Choices.CollectionChanged -= OnChoicesChanged;
            DetachRows();
        };
    }

    internal CollectionPickerDialogViewModel ViewModel { get; }

    private void OnChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    private void RebuildRows()
    {
        DetachRows();
        ChoicesPanel.Children.Clear();
        foreach (var choice in ViewModel.Choices) ChoicesPanel.Children.Add(CreateRow(choice));
    }

    private void DetachRows()
    {
        foreach (var row in _rows) row.Choice.PropertyChanged -= OnChoiceChanged;
        _rows.Clear();
    }

    private Button CreateRow(CollectionChoiceViewModel choice)
    {
        var name = new TextBlock { Text = choice.Collection.Name, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        var count = new TextBlock { Text = choice.CountText, Style = (Style)Application.Current.Resources["MetaTextStyle"] };
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(name);
        text.Children.Add(count);

        var icon = new LucideIcon
        {
            Data = BindHelpers.Icon("Icon.Check"),
            Size = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeResources.GetBrush("LumaOnAccentBrush")
        };
        var check = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center, Child = icon };
        Grid.SetColumn(check, 1);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        grid.Children.Add(check);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["LumaButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(12),
            Content = grid,
            Command = ViewModel.ToggleCommand,
            CommandParameter = choice
        };
        AutomationProperties.SetName(button, choice.Collection.Name);

        _rows.Add((choice, check, icon));
        choice.PropertyChanged += OnChoiceChanged;
        UpdateCheck(choice, check, icon);
        return button;
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CollectionChoiceViewModel.IsMember)) return;
        foreach (var row in _rows)
        {
            if (ReferenceEquals(row.Choice, sender)) UpdateCheck(row.Choice, row.Check, row.Icon);
        }
    }

    private static void UpdateCheck(CollectionChoiceViewModel choice, Border check, LucideIcon icon)
    {
        check.Background = BindHelpers.CheckBrush(choice.IsMember);
        icon.Visibility = choice.IsMember ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNewNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ViewModel.CreateCommand.CanExecute(null)) ViewModel.CreateCommand.Execute(null);
    }
}
