using Luma.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Luma.App.Controls;

public enum ImageLoadState
{
    Idle,
    Loading,
    Loaded,
    Failed
}

/// <summary>
/// Lazily loads its image only while it is loaded in the visual tree. When a virtualised card scrolls away (Unloaded)
/// the pending request is cancelled and the bitmap lease is returned, so off-screen cards cost (almost) no memory.
/// The image covers the control (<c>UniformToFill</c>, centred crop) and honours <see cref="Control.CornerRadius"/>.
/// </summary>
public sealed partial class ThumbnailImage : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null, (d, _) => ((ThumbnailImage)d).OnSourceChanged()));

    public static readonly DependencyProperty FallbackSourceProperty = DependencyProperty.Register(
        nameof(FallbackSource), typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth), typeof(int), typeof(ThumbnailImage), new PropertyMetadata(420));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ImageLoadState), typeof(ThumbnailImage), new PropertyMetadata(ImageLoadState.Idle, (d, _) => ((ThumbnailImage)d).OnStateChanged()));

    public static readonly DependencyProperty IsFailedProperty = DependencyProperty.Register(
        nameof(IsFailed), typeof(bool), typeof(ThumbnailImage), new PropertyMetadata(false));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(ThumbnailImage), new PropertyMetadata(false));

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(220);

    private readonly ImageBrush _brush;
    private readonly ScalarTransition _fade = new() { Duration = FadeDuration };
    private CancellationTokenSource? _cts;
    private ImageLease? _lease;
    private bool _attached;

    public ThumbnailImage()
    {
        IsHitTestVisible = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _brush = new ImageBrush { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
        var surface = new Border { Background = _brush };
        // The brush is clipped by the border's rounded corners, so cards can round the image without extra clipping.
        surface.SetBinding(Border.CornerRadiusProperty, new Binding { Source = this, Path = new PropertyPath(nameof(CornerRadius)) });
        Content = surface;
        OpacityTransition = _fade;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Image address (URL or local path) passed to <see cref="ImageLoader.AcquireThumbnailAsync"/>.</summary>
    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Tried once when <see cref="Source"/> fails to load.</summary>
    public string? FallbackSource { get => (string?)GetValue(FallbackSourceProperty); set => SetValue(FallbackSourceProperty, value); }

    /// <summary>Decode width in pixels (default 420). Never upscales.</summary>
    public int DecodeWidth { get => (int)GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }

    public ImageLoadState State { get => (ImageLoadState)GetValue(StateProperty); private set => SetValue(StateProperty, value); }

    /// <summary>True when the image could not be loaded (lets templates show a calm fallback).</summary>
    public bool IsFailed { get => (bool)GetValue(IsFailedProperty); private set => SetValue(IsFailedProperty, value); }

    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); private set => SetValue(IsLoadingProperty, value); }

    private void OnStateChanged()
    {
        IsFailed = State == ImageLoadState.Failed;
        IsLoading = State == ImageLoadState.Loading;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Loaded/Unloaded can arrive out of order when an element is re-parented; only release when really gone.
        if (IsLoaded) return;
        _attached = false;
        Release();
    }

    private void OnSourceChanged()
    {
        if (!_attached) return;
        Release();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var source = Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            State = ImageLoadState.Idle;
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        State = ImageLoadState.Loading;
        ImageLoader loader;
        try
        {
            loader = LumaServices.Get<ImageLoader>();
        }
        catch (InvalidOperationException)
        {
            State = ImageLoadState.Idle; // designer / services not ready
            return;
        }

        try
        {
            var pending = AcquireWithFallbackAsync(loader, source, cts.Token);
            if (!pending.IsCompleted) HideInstantly(); // fade in only when the image had to be fetched
            var lease = await pending;
            if (cts.IsCancellationRequested)
            {
                lease.Dispose();
                return;
            }

            var previous = _lease;
            _lease = lease;
            _brush.ImageSource = lease.Source;
            previous?.Dispose();
            State = ImageLoadState.Loaded;
            Opacity = 1;
        }
        catch (OperationCanceledException)
        {
            // scrolled away or source replaced
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            State = ImageLoadState.Failed;
            Opacity = 1;
            try
            {
                LumaServices.Get<ILogger<ThumbnailImage>>().LogDebug(ex, "Thumbnail failed to load: {Source}", source);
            }
            catch (InvalidOperationException)
            {
                // logging unavailable
            }
        }
    }

    private async Task<ImageLease> AcquireWithFallbackAsync(ImageLoader loader, string source, CancellationToken ct)
    {
        var fallback = FallbackSource;
        try
        {
            return await loader.AcquireThumbnailAsync(source, DecodeWidth, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(fallback) && fallback != source)
        {
            return await loader.AcquireThumbnailAsync(fallback, DecodeWidth, ct);
        }
    }

    /// <summary>Drops to transparent without animating, so only the fade-in is visible.</summary>
    private void HideInstantly()
    {
        OpacityTransition = null;
        Opacity = 0;
        OpacityTransition = _fade;
    }

    private void Release()
    {
        _cts?.Cancel();
        _cts = null;
        // Detach before releasing: the last release disposes the SoftwareBitmapSource.
        _brush.ImageSource = null;
        _lease?.Dispose();
        _lease = null;
        State = ImageLoadState.Idle;
    }
}
