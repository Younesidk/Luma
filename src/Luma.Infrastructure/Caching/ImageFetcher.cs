using System.Security.Cryptography;
using System.Text;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Luma.Infrastructure.Providers;

namespace Luma.Infrastructure.Caching;

/// <summary>
/// Downloads thumbnails with bounded concurrency, de-duplicates identical in-flight requests (cancelling the transfer
/// once nobody is waiting for it any more) and keeps a size-limited on-disk cache that evicts least recently used files.
/// </summary>
public sealed class ImageFetcher : IImageFetcher, IDisposable
{
    public const string HttpClientName = "images";
    private const long MaxThumbnailBytes = 6L * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILumaPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<ImageFetcher> _logger;
    private readonly SemaphoreSlim _networkGate;
    private readonly SemaphoreSlim _previewGate = new(2, 2);
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private readonly object _flightLock = new();
    private readonly object _cacheLock = new();
    private long _cacheBytes = -1;
    private int _evicting;

    public ImageFetcher(IHttpClientFactory httpFactory, ILumaPaths paths, ISettingsService settings, IOptions<LumaOptions> options, ILogger<ImageFetcher> logger)
    {
        _httpFactory = httpFactory;
        _paths = paths;
        _settings = settings;
        _logger = logger;
        _networkGate = new SemaphoreSlim(Math.Clamp(options.Value.ThumbnailConcurrency, 1, 16));
    }

    public async Task<byte[]> GetThumbnailAsync(string url, CancellationToken cancellationToken = default)
    {
        EnsureHttpUrl(url);
        var file = CachePath(url);
        var cached = await TryReadCacheAsync(file, cancellationToken);
        if (cached is not null) return cached;

        InFlight flight;
        lock (_flightLock)
        {
            if (!_inFlight.TryGetValue(url, out flight!))
            {
                flight = StartFlight(url, file);
                _inFlight[url] = flight;
            }
            flight.Waiters++;
        }

        try
        {
            return await flight.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            bool abandoned;
            lock (_flightLock) abandoned = --flight.Waiters == 0;
            if (abandoned && !flight.Task.IsCompleted)
            {
                // Nobody wants this image anymore (e.g. the card scrolled away): stop the transfer.
                try { flight.Cts.Cancel(); }
                catch (ObjectDisposedException) { /* transfer already finished */ }
            }
        }
    }

    public async Task<byte[]> GetImageAsync(string url, long maxBytes, CancellationToken cancellationToken = default)
    {
        EnsureHttpUrl(url);
        await _previewGate.WaitAsync(cancellationToken);
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            return await DownloadBytesAsync(client, url, maxBytes, cancellationToken);
        }
        finally
        {
            _previewGate.Release();
        }
    }

    public long GetCacheSizeBytes()
    {
        EnsureCacheSizeKnown();
        return Interlocked.Read(ref _cacheBytes);
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_cacheLock)
        {
            if (Directory.Exists(_paths.ThumbnailCacheDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.ThumbnailCacheDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Could not delete cache file"); }
                }
            }
            Interlocked.Exchange(ref _cacheBytes, 0);
        }
    }, cancellationToken);

    private InFlight StartFlight(string url, string file)
    {
        var cts = new CancellationTokenSource();
        var flight = new InFlight(cts);
        flight.Task = Task.Run(async () =>
        {
            try
            {
                await _networkGate.WaitAsync(cts.Token);
                try
                {
                    var client = _httpFactory.CreateClient(HttpClientName);
                    var bytes = await DownloadBytesAsync(client, url, MaxThumbnailBytes, cts.Token);
                    using (var ms = new MemoryStream(bytes, writable: false))
                    {
                        if (ImageInfoReader.TryRead(ms) is null)
                            throw new InvalidImageException("The server did not return a valid image.");
                    }
                    await WriteCacheAsync(file, bytes);
                    return bytes;
                }
                finally
                {
                    _networkGate.Release();
                }
            }
            finally
            {
                lock (_flightLock) _inFlight.Remove(url);
                cts.Dispose();
            }
        });
        return flight;
    }

    private static async Task<byte[]> DownloadBytesAsync(HttpClient client, string url, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new LumaException($"The image server answered with HTTP {(int)response.StatusCode}.");

            var length = response.Content.Headers.ContentLength;
            if (length > maxBytes) throw new LumaException("The image is larger than Luma is willing to load for display.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(length is > 0 ? (int)Math.Min(length.Value, maxBytes) : 64 * 1024);
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new LumaException("The image is larger than Luma is willing to load for display.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new LumaException("Couldn't download the image. Check your internet connection.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LumaException("The image server took too long to respond.");
        }
    }

    private async Task<byte[]?> TryReadCacheAsync(string file, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var bytes = await File.ReadAllBytesAsync(file, ct);
            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } // LRU bookkeeping
            catch (IOException) { /* ignore */ }
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(string file, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_paths.ThumbnailCacheDirectory);
            var temp = file + ".tmp" + Environment.CurrentManagedThreadId;
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, file, overwrite: true);
            EnsureCacheSizeKnown();
            var total = Interlocked.Add(ref _cacheBytes, bytes.Length);
            var limit = _settings.Current.ThumbnailCacheMegabytes * 1024L * 1024L;
            if (total > limit && Interlocked.CompareExchange(ref _evicting, 1, 0) == 0)
            {
                _ = Task.Run(() => Evict(limit));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation; a full disk must not break browsing.
            _logger.LogDebug(ex, "Could not write thumbnail cache entry");
        }
    }

    private void EnsureCacheSizeKnown()
    {
        if (Interlocked.Read(ref _cacheBytes) >= 0) return;
        lock (_cacheLock)
        {
            if (_cacheBytes >= 0) return;
            long total = 0;
            if (Directory.Exists(_paths.ThumbnailCacheDirectory))
            {
                foreach (var file in new DirectoryInfo(_paths.ThumbnailCacheDirectory).EnumerateFiles())
                    total += file.Length;
            }
            Interlocked.Exchange(ref _cacheBytes, total);
        }
    }

    private void Evict(long limitBytes)
    {
        try
        {
            lock (_cacheLock)
            {
                var files = new DirectoryInfo(_paths.ThumbnailCacheDirectory).EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
                var total = files.Sum(f => f.Length);
                var target = (long)(limitBytes * 0.8);
                foreach (var file in files)
                {
                    if (total <= target) break;
                    try
                    {
                        total -= file.Length;
                        file.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        total += file.Length;
                    }
                }
                Interlocked.Exchange(ref _cacheBytes, total);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail cache eviction failed");
        }
        finally
        {
            Interlocked.Exchange(ref _evicting, 0);
        }
    }

    private string CachePath(string url)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Path.Combine(_paths.ThumbnailCacheDirectory, Convert.ToHexString(hash).ToLowerInvariant() + ".img");
    }

    private static void EnsureHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new LumaException("Only http and https image addresses are supported.");
    }

    public void Dispose()
    {
        _networkGate.Dispose();
        _previewGate.Dispose();
    }

    private sealed class InFlight(CancellationTokenSource cts)
    {
        public readonly CancellationTokenSource Cts = cts;
        public Task<byte[]> Task = null!;
        public int Waiters;
    }
}
