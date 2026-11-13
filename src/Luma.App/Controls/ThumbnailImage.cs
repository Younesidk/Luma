using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Luma.App.Services;
using Microsoft.Extensions.Logging;

namespace Luma.App.Controls;

public enum ImageLoadState
{
    Idle,
    Loading,
    Loaded,
    Failed
}

/// <summary>
/// Lazily loads its image only while it is part of the visual tree. When a virtualised row scrolls away the pending
/// request is cancelled and the bitmap lease is returned, so off-screen cards cost (almost) no memory.
/// </summary>
public sealed class ThumbnailImage : Control
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<ThumbnailImage, string?>(nameof(Source));
    public static readonly StyledProperty<string?> FallbackSourceProperty = AvaloniaProperty.Register<ThumbnailImage, string?>(nameof(FallbackSource));
    public static readonly StyledProperty<int> DecodeWidthProperty = AvaloniaProperty.Register<ThumbnailImage, int>(nameof(DecodeWidth), 420);
    public static readonly StyledProperty<ImageLoadState> StateProperty = AvaloniaProperty.Register<ThumbnailImage, ImageLoadState>(nameof(State));

    private CancellationTokenSource? _cts;
    private ImageLease? _lease;
    private bool _attached;

    static ThumbnailImage()
    {
        AffectsRender<ThumbnailImage>(StateProperty);
        SourceProperty.Changed.AddClassHandler<ThumbnailImage>((image, _) => image.OnSourceChanged());
    }

    public ThumbnailImage()
    {
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }];
        IsHitTestVisible = false;
    }

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public int DecodeWidth { get => GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }

    /// <summary>Tried once when <see cref="Source"/> fails to load.</summary>
    public string? FallbackSource { get => GetValue(FallbackSourceProperty); set => SetValue(FallbackSourceProperty, value); }

    public static readonly StyledProperty<bool> IsFailedProperty = AvaloniaProperty.Register<ThumbnailImage, bool>(nameof(IsFailed));
    public static readonly StyledProperty<bool> IsLoadingProperty = AvaloniaProperty.Register<ThumbnailImage, bool>(nameof(IsLoading));

    public ImageLoadState State { get => GetValue(StateProperty); private set => SetValue(StateProperty, value); }

    /// <summary>True when the image could not be loaded (lets templates show a calm fallback).</summary>
    public bool IsFailed { get => GetValue(IsFailedProperty); private set => SetValue(IsFailedProperty, value); }

    public bool IsLoading { get => GetValue(IsLoadingProperty); private set => SetValue(IsLoadingProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty)
        {
            IsFailed = State == ImageLoadState.Failed;
            IsLoading = State == ImageLoadState.Loading;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _ = LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
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
        var loader = LumaServices.Get<ImageLoader>();
        try
        {
            var pending = AcquireWithFallbackAsync(loader, source, cts.Token);
            if (!pending.IsCompleted) Opacity = 0; // fade in only when the image had to be fetched
            var lease = await pending;
            if (cts.IsCancellationRequested)
            {
                lease.Dispose();
                return;
            }

            _lease?.Dispose();
            _lease = lease;
            State = ImageLoadState.Loaded;
            Opacity = 1;
            InvalidateVisual();
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
            LumaServices.Get<ILogger<ThumbnailImage>>().LogDebug(ex, "Thumbnail failed to load: {Source}", source);
        }
    }

    private async Task<ImageLease> AcquireWithFallbackAsync(ImageLoader loader, string source, CancellationToken ct)
    {
        try
        {
            return await loader.AcquireThumbnailAsync(source, DecodeWidth, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(FallbackSource) && FallbackSource != source)
        {
            return await loader.AcquireThumbnailAsync(FallbackSource!, DecodeWidth, ct);
        }
    }

    private void Release()
    {
        _cts?.Cancel();
        _cts = null;
        _lease?.Dispose();
        _lease = null;
        State = ImageLoadState.Idle;
    }

    public override void Render(DrawingContext context)
    {
        if (_lease is null) return;
        var bitmap = _lease.Bitmap;
        var dest = new Rect(Bounds.Size);
        if (dest.Width <= 0 || dest.Height <= 0 || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0) return;

        // "UniformToFill": crop the source so it covers the destination without distortion.
        var scale = Math.Max(dest.Width / bitmap.Size.Width, dest.Height / bitmap.Size.Height);
        var sw = dest.Width / scale;
        var sh = dest.Height / scale;
        var source = new Rect((bitmap.Size.Width - sw) / 2, (bitmap.Size.Height - sh) / 2, sw, sh);
        context.DrawImage(bitmap, source, dest);
    }
}





