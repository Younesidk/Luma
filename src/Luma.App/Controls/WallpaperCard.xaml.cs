using System.Numerics;
using Luma.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Luma.App.Controls;

/// <summary>
/// Gallery card for one <see cref="WallpaperItemViewModel"/> (its DataContext). Hovering scales the card slightly and
/// reveals the scrims, metadata and quick actions; keyboard focus inside the card reveals them too. The favourite heart
/// stays visible while the wallpaper is a favourite.
/// </summary>
public sealed partial class WallpaperCard : UserControl
{
    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(WallpaperCard), new PropertyMetadata(false));

    private const float HoverScale = 1.018f;
    private const float PressedScale = 1.004f;
    private const float CornerRadiusPx = 14f;

    private static readonly SolidColorBrush FavoriteBrush = new(ColorHelper.FromArgb(0xFF, 0xFF, 0x7A, 0x93));
    private static readonly SolidColorBrush GlassForegroundBrush = new(ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xFA));

    private bool _pointerOver;
    private bool _focusWithin;
    private bool _pressed;
    private CompositionRoundedRectangleGeometry? _clipGeometry;

    public WallpaperCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        Card.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        Card.SizeChanged += (_, e) =>
        {
            Card.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            UpdateClip(e.NewSize);
        };

        // Buttons mark pointer events handled; listen to handled ones too.
        AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => { _pointerOver = true; UpdateVisuals(); }), true);
        AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => { _pointerOver = false; _pressed = false; UpdateVisuals(); }), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => { _pointerOver = false; _pressed = false; UpdateVisuals(); }), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { _pressed = false; UpdateVisuals(); }), true);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = true; UpdateVisuals(); }
        }), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _pressed = false; UpdateVisuals(); }), true);
        GotFocus += (_, _) => { _focusWithin = true; UpdateVisuals(); };
        LostFocus += (_, _) => { _focusWithin = HasFocusWithin(); UpdateVisuals(); };
    }

    /// <summary>
    /// The card's view model (DataContext) for compiled bindings. Internal on purpose: a public property would make the
    /// XAML compiler emit type metadata for the view model's Core model (<c>Wallpaper</c>, which has required members).
    /// </summary>
    internal WallpaperItemViewModel? ViewModel { get; private set; }

    /// <summary>True while hovered or while keyboard focus is inside the card (drives the reveal animations).</summary>
    public bool IsRevealed { get => (bool)GetValue(IsRevealedProperty); private set => SetValue(IsRevealedProperty, value); }

    public double RevealOpacity(bool revealed) => revealed ? 1 : 0;

    public double HeartOpacity(bool isFavorite, bool revealed) => isFavorite || revealed ? 1 : 0;

    public Brush HeartBrush(bool isFavorite) => isFavorite ? FavoriteBrush : GlassForegroundBrush;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var vm = args.NewValue as WallpaperItemViewModel;
        if (ReferenceEquals(vm, ViewModel)) return;
        ViewModel = vm;
        // Recycled by the grid: start from rest.
        _pointerOver = _pressed = _focusWithin = false;
        UpdateVisuals();
        Bindings.Update();
    }

    private void UpdateVisuals()
    {
        IsRevealed = _pointerOver || _focusWithin;
        var scale = _pressed ? PressedScale : _pointerOver ? HoverScale : 1f;
        Card.Scale = new Vector3(scale, scale, 1);
    }

    private bool HasFocusWithin()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused) return false;
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this)) return true;
        }
        return false;
    }

    /// <summary>Rounded clip for everything inside the card (image, scrims and the progress bar at the bottom edge).</summary>
    private void UpdateClip(Windows.Foundation.Size size)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Surface);
        var compositor = visual.Compositor;
        if (_clipGeometry is null)
        {
            _clipGeometry = compositor.CreateRoundedRectangleGeometry();
            _clipGeometry.CornerRadius = new Vector2(CornerRadiusPx, CornerRadiusPx);
            visual.Clip = compositor.CreateGeometricClip(_clipGeometry);
        }
        _clipGeometry.Size = new Vector2((float)size.Width, (float)size.Height);
    }
}
