using System.Numerics;
using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Luma.App.Views;

/// <summary>
/// One collection in the Collections overview: a single cover or a 2×2 mosaic, name and count. It scales up slightly on
/// hover; click opens it and the context menu offers Open / Edit / Delete (commands on <see cref="Owner"/>).
/// </summary>
public sealed partial class CollectionCard : UserControl
{
    private const float HoverScale = 1.015f;

    public CollectionCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        CardButton.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        CardButton.SizeChanged += (_, e) => CardButton.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        // The button marks pointer events handled; listen to handled ones too.
        CardButton.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => SetHover(true)), true);
        CardButton.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => SetHover(false)), true);
        CardButton.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => SetHover(false)), true);
    }

    /// <summary>Internal: the card view model exposes a Core model (see WallpaperCard.ViewModel).</summary>
    internal CollectionCardViewModel? ViewModel { get; private set; }

    /// <summary>The page view model whose commands the card invokes (set by CollectionsView when the card is prepared).</summary>
    internal CollectionsViewModel? Owner { get; set; }

    private void SetHover(bool hover)
    {
        var scale = hover ? HoverScale : 1f;
        CardButton.Scale = new Vector3(scale, scale, 1);
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = args.NewValue as CollectionCardViewModel;
        if (ReferenceEquals(vm, ViewModel)) return;
        ViewModel = vm;
        SetHover(false);
        Bindings.Update();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } card && Owner is { } owner) owner.OpenCommand.Execute(card);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } card && Owner is { } owner) owner.RenameCommand.Execute(card);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } card && Owner is { } owner) owner.DeleteCommand.Execute(card);
    }
}
