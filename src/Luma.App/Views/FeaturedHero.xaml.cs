using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>Discover's featured wallpaper banner, shown above the gallery.</summary>
public sealed partial class FeaturedHero : UserControl
{
    private DiscoverViewModel? _viewModel;
    private XamlRoot? _root;

    public FeaturedHero()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _root = XamlRoot;
            if (_root is null) return;
            _root.Changed += OnRootChanged;
            ApplyHeight(_root.Size.Height);
        };
        Unloaded += (_, _) =>
        {
            if (_root is not null) _root.Changed -= OnRootChanged;
            _root = null;
        };
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal DiscoverViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyHeight(sender.Size.Height);

    /// <summary>On a short window a fixed 300px hero filled the whole gallery viewport; scale it with the window height.</summary>
    private void ApplyHeight(double windowHeight)
    {
        if (windowHeight <= 0) return;
        var height = Math.Round(Math.Clamp((windowHeight - 140) * 0.42, 220, 300));
        if (Math.Abs(Card.Height - height) > 0.5) Card.Height = height;
    }
}
