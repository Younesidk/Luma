using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Luma.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luma.App.Services;

/// <summary>
/// Decodes images for display with a small, strictly bounded memory footprint:
/// bitmaps are decoded straight to the display width, decoding is limited to a few parallel jobs,
/// and decoded thumbnails live in an LRU cache that is evicted by count and by bytes. A bitmap that is still on screen
/// (leased) is never disposed; it is released once the last lease is returned.
/// </summary>
public sealed class ImageLoader
{
    private const long BytesPerItemBudget = 600 * 1024;

    private readonly IImageFetcher _fetcher;
    private readonly ISettingsService _settings;
    private readonly ILogger<ImageLoader> _logger;
    private readonly SemaphoreSlim _decodeGate = new(3, 3);
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public ImageLoader(IImageFetcher fetcher, ISettingsService settings, ILogger<ImageLoader> logger)
    {
        _fetcher = fetcher;
        _settings = settings;
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

        var bitmap = await DecodeAsync(source, decodeWidth, ct);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var raced))
            {
                // Someone else decoded the same image while we were busy: keep theirs.
                bitmap.Dispose();
                raced.RefCount++;
                return new ImageLease(this, raced);
            }

            var entry = new Entry(key, bitmap) { RefCount = 1, Cached = true };
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
        await _decodeGate.WaitAsync(ct);
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
            }, ct);
            return new ImageLease(this, new Entry("preview", bitmap) { RefCount = 1, Cached = false });
        }
        finally
        {
            _decodeGate.Release();
        }
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
                if (entry.RefCount == 0) entry.Bitmap.Dispose();
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
                entry.Bitmap.Dispose();
                return;
            }
            EvictLocked();
        }
    }

    private async Task<Bitmap> DecodeAsync(string source, int decodeWidth, CancellationToken ct)
    {
        Stream stream;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var bytes = await _fetcher.GetThumbnailAsync(source, ct);
            stream = new MemoryStream(bytes, writable: false);
        }
        else
        {
            stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        }

        await using (stream)
        {
            await _decodeGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                return await Task.Run(() => Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality), ct);
            }
            finally
            {
                _decodeGate.Release();
            }
        }
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
                entry.Bitmap.Dispose();
            }
            node = previous;
        }
    }

    internal sealed class Entry(string key, Bitmap bitmap)
    {
        public string Key { get; } = key;
        public Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        public int RefCount { get; set; }
        public bool Cached { get; set; }
        public LinkedListNode<Entry> Node { get; set; } = null!;
    }
}

/// <summary>Keeps a decoded bitmap alive while it is displayed.</summary>
public sealed class ImageLease : IDisposable
{
    private readonly ImageLoader _loader;
    private ImageLoader.Entry? _entry;

    internal ImageLease(ImageLoader loader, ImageLoader.Entry entry)
    {
        _loader = loader;
        _entry = entry;
        Bitmap = entry.Bitmap;
    }

    public Bitmap Bitmap { get; }

    public void Dispose()
    {
        var entry = Interlocked.Exchange(ref _entry, null);
        if (entry is not null) _loader.Release(entry);
    }
}
