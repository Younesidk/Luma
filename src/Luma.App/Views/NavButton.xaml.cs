using System.ComponentModel;
using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Luma.App.Views;

/// <summary>
/// One sidebar entry (DataContext: <see cref="NavItemViewModel"/>). The active entry gets the accent-subtle fill, primary
/// text and an accent icon; in the collapsed sidebar only the icon shows (the tooltip carries the title).
/// </summary>
public sealed partial class NavButton : UserControl
{
    public NavButton()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ActualThemeChanged += (_, _) => UpdateActive();
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as NavItemViewModel);
    }

    /// <summary>The entry's view model for compiled bindings.</summary>
    internal NavItemViewModel? ViewModel { get; private set; }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => Attach(args.NewValue as NavItemViewModel);

    private void Attach(NavItemViewModel? vm)
    {
        if (ReferenceEquals(vm, ViewModel))
        {
            UpdateActive();
            return;
        }
        if (ViewModel is not null) ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel = vm;
        if (vm is not null) vm.PropertyChanged += OnViewModelChanged;
        Bindings.Update();
        UpdateActive();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavItemViewModel.IsActive)) UpdateActive();
    }

    private void UpdateActive()
    {
        var active = ViewModel?.IsActive == true;
        if (active)
        {
            Item.Background = ThemeResources.GetBrush("LumaAccentSubtleBrush");
            Item.Foreground = ThemeResources.GetBrush("LumaTextPrimaryBrush");
            NavIcon.Foreground = ThemeResources.GetBrush("LumaAccentBrush");
        }
        else
        {
            Item.ClearValue(BackgroundProperty);
            Item.ClearValue(ForegroundProperty);
            NavIcon.ClearValue(ForegroundProperty);
        }

        // The active entry keeps its plain accent fill: the hover/press tint layer only shows on inactive entries.
        if (FindHoverFill() is { } hover) hover.Opacity = active ? 0 : 1;
    }

    private Border? FindHoverFill()
    {
        Item.ApplyTemplate();
        return VisualTreeHelper.GetChildrenCount(Item) > 0 && VisualTreeHelper.GetChild(Item, 0) is Panel root
            ? root.Children.OfType<Border>().FirstOrDefault(b => b.Name == "HoverFill")
            : null;
    }
}
