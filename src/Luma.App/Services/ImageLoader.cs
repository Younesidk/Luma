using Luma.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Luma.App.Services;

/// <summary>
/// Decodes images for display with a small, strictly bounded memory footprint:
/// bitmaps are decoded (by WIC, off the UI thread) straight to the display width, decoding is limited to a few parallel jobs,
/// and decoded thumbnails live in an LRU cache that is evicted by count and by bytes. A bitmap that is still on screen
/// (leased) is never disposed; it is released once the last lease is returned.
/// </summary>
public sealed class ImageLoader
{
    private const long BytesPerItemBudget = 600 * 1024;

    private readonly IImageFetcher _fetcher;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ImageLoader> _logger;
    private readonly SemaphoreSlim _decodeGate = new(3, 3);
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public ImageLoader(IImageFetcher fetcher, ISettingsService settings, IUiDispatcher dispatcher, ILogger<ImageLoader> logger)
    {
        _fetcher = fetcher;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public int CachedCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    public long CachedBytes
    {
        get { lock (_lock) return _bytes; }
    }

    /// <summary>Loads (or reuses) a thumbnail. The returned lease must be disposed when the image leaves the screen.</summary>
    public async Task<ImageLease> AcquireThumbnailAsync(string source, int decodeWidth, CancellationToken ct)
    {
        var key = $"{decodeWidth}|{source}";
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                hit.RefCount++;
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node);
                return new ImageLease(this, hit);
            }
        }

        byte[] bytes;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            bytes = await _fetcher.GetThumbnailAsync(source, ct);
        else
            bytes = await File.ReadAllBytesAsync(source, ct);

        var decoded = await DecodeAsync(bytes, decodeWidth, BitmapInterpolationMode.Linear, ct);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var raced))
            {
                // Someone else decoded the same image while we were busy: keep theirs.
                decoded.Dispose();
                raced.RefCount++;
                return new ImageLease(this, raced);
            }

            var entry = new Entry(key, decoded) { RefCount = 1, Cached = true };
            entry.Node = new LinkedListNode<Entry>(entry);
            _entries[key] = entry;
            _lru.AddFirst(entry.Node);
            _bytes += entry.Bytes;
            EvictLocked();
            return new ImageLease(this, entry);
        }
    }

    /// <summary>Loads one larger image for the preview. It is not cached; disposing the lease frees it.</summary>
    public async Task<ImageLease> AcquirePreviewAsync(string url, int decodeWidth, CancellationToken ct)
    {
        var isRemote = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var bytes = isRemote
            ? await _fetcher.GetImageAsync(url, 30L * 1024 * 1024, ct)
            : await File.ReadAllBytesAsync(url, ct);
        var decoded = await DecodeAsync(bytes, decodeWidth, BitmapInterpolationMode.Fant, ct);
        return new ImageLease(this, new Entry("preview", decoded) { RefCount = 1, Cached = false });
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values.ToList())
            {
                _lru.Remove(entry.Node);
                _entries.Remove(entry.Key);
                _bytes -= entry.Bytes;
                if (entry.RefCount == 0) entry.Image.Dispose();
                else entry.Cached = false; // still on screen: disposed when released
            }
        }
    }

    internal void Release(Entry entry)
    {
        lock (_lock)
        {
            entry.RefCount--;
            if (entry.RefCount > 0) return;
            if (!entry.Cached)
            {
                entry.Image.Dispose();
                return;
            }
            EvictLocked();
        }
    }

    /// <summary>
    /// Decodes with WIC on the thread pool into a BGRA8 premultiplied bitmap no wider than <paramref name="decodeWidth"/>
    /// (aspect ratio preserved, EXIF orientation applied), then hands it to a <see cref="SoftwareBitmapSource"/> on the UI thread.
    /// </summary>
    private async Task<DecodedImage> DecodeAsync(byte[] bytes, int decodeWidth, BitmapInterpolationMode interpolation, CancellationToken ct)
    {
        SoftwareBitmap bitmap;
        await _decodeGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            bitmap = await Task.Run(() => DecodeBitmapAsync(bytes, decodeWidth, interpolation), ct);
        }
        finally
        {
            _decodeGate.Release();
        }

        SoftwareBitmapSource? source = null;
        try
        {
            Task? setBitmap = null;
            await _dispatcher.InvokeAsync(() =>
            {
                source = new SoftwareBitmapSource();
                setBitmap = source.SetBitmapAsync(bitmap).AsTask();
            });
            await setBitmap!;
            // The bitmap must outlive the source: XAML reads it again when it re-renders (for example after a device
            // loss), and a closed bitmap makes it fail-fast with RO_E_CLOSED (0x80000013).
            return new DecodedImage(bitmap, source!, _dispatcher);
        }
        catch
        {
            var orphan = source;
            _dispatcher.Post(() =>
            {
                orphan?.Dispose();
                bitmap.Dispose();
            });
            throw;
        }
    }

    private static async Task<SoftwareBitmap> DecodeBitmapAsync(byte[] bytes, int decodeWidth, BitmapInterpolationMode interpolation)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var orientedWidth = Math.Max(1u, decoder.OrientedPixelWidth);
        var scale = decodeWidth > 0 ? Math.Min(1.0, decodeWidth / (double)orientedWidth) : 1.0;
        // ScaledWidth/Height apply to the stored (un-oriented) frame; the scale factor is the same either way.
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = interpolation
        };
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
    }

    private void EvictLocked()
    {
        var maxItems = Math.Max(30, _settings.Current.MemoryCacheItems);
        var maxBytes = maxItems * BytesPerItemBudget;
        var node = _lru.Last;
        while (node is not null && (_entries.Count > maxItems || _bytes > maxBytes))
        {
            var previous = node.Previous;
            var entry = node.Value;
            if (entry.RefCount == 0)
            {
                _lru.Remove(node);
                _entries.Remove(entry.Key);
                _bytes -= entry.Bytes;
                entry.Image.Dispose();
            }
            node = previous;
        }
    }

    internal sealed class Entry(string key, DecodedImage image)
    {
        public string Key { get; } = key;
        public DecodedImage Image { get; } = image;
        public long Bytes { get; } = (long)image.PixelWidth * image.PixelHeight * 4;
        public int RefCount { get; set; }
        public bool Cached { get; set; }
        public LinkedListNode<Entry> Node { get; set; } = null!;
    }

    /// <summary>
    /// A decoded bitmap and the XAML source showing it. Both are freed together, exactly once, on the UI thread:
    /// the source first, then the bitmap it reads from.
    /// </summary>
    internal sealed class DecodedImage(SoftwareBitmap bitmap, SoftwareBitmapSource source, IUiDispatcher dispatcher) : IDisposable
    {
        private int _disposed;

        public SoftwareBitmapSource Source { get; } = source;
        public int PixelWidth { get; } = bitmap.PixelWidth;
        public int PixelHeight { get; } = bitmap.PixelHeight;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var source = Source;
            dispatcher.Post(() =>
            {
                source.Dispose();
                bitmap.Dispose();
            });
        }
    }
}

/// <summary>Keeps a decoded image alive while it is displayed.</summary>
public sealed class ImageLease : IDisposable
{
    private readonly ImageLoader _loader;
    private ImageLoader.Entry? _entry;

    internal ImageLease(ImageLoader loader, ImageLoader.Entry entry)
    {
        _loader = loader;
        _entry = entry;
        Source = entry.Image.Source;
        PixelWidth = entry.Image.PixelWidth;
        PixelHeight = entry.Image.PixelHeight;
    }

    /// <summary>The image to assign to <c>Image.Source</c> (UI thread only).</summary>
    public ImageSource Source { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public void Dispose()
    {
        var entry = Interlocked.Exchange(ref _entry, null);
        if (entry is not null) _loader.Release(entry);
    }
}
