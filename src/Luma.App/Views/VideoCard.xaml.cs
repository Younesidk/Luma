using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>One imported video in the Live wallpapers gallery (DataContext = <see cref="VideoItemViewModel"/>).</summary>
public sealed partial class VideoCard : UserControl
{
    public VideoCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Internal: the card view model exposes a Core model (see WallpaperCard.ViewModel).</summary>
    internal VideoItemViewModel? ViewModel { get; private set; }

    public string PreviewName(string? title) => $"Preview {title}";

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = args.NewValue as VideoItemViewModel;
        if (ReferenceEquals(vm, ViewModel)) return;
        ViewModel = vm;
        Bindings.Update();
    }
}
