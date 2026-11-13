using System.Security.Cryptography;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Downloads;

/// <summary>
/// Queue-based downloader: bounded concurrency, progress, cancellation, validation of the received file,
/// duplicate detection (by wallpaper and by content hash) and friendly error messages.
/// </summary>
public sealed class DownloadService : IDownloadService
{
    public const string HttpClientName = "downloads";
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly IDownloadLibrary _library;
    private readonly ILogger<DownloadService> _logger;
    private readonly object _lock = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly Queue<DownloadJob> _queue = new();
    private int _running;

    public DownloadService(IHttpClientFactory httpFactory, ISettingsService settings, IDownloadLibrary library, ILogger<DownloadService> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _library = library;
        _logger = logger;
    }

    public event EventHandler<DownloadJob>? JobAdded;

    public IReadOnlyList<DownloadJob> Jobs
    {
        get
        {
            lock (_lock) return _jobs.ToList();
        }
    }

    public DownloadJob Enqueue(Wallpaper wallpaper)
    {
        DownloadJob job;
        lock (_lock)
        {
            var existing = _jobs.FirstOrDefault(j => !j.IsFinished && j.Wallpaper.Key == wallpaper.Key);
            if (existing is not null) return existing;
            job = new DownloadJob(wallpaper);
            _jobs.Add(job);
            _queue.Enqueue(job);
        }

        JobAdded?.Invoke(this, job);
        PumpQueue();
        return job;
    }

    public void ClearFinished()
    {
        lock (_lock) _jobs.RemoveAll(j => j.IsFinished);
    }

    private void PumpQueue()
    {
        while (true)
        {
            DownloadJob? next;
            lock (_lock)
            {
                if (_running >= _settings.Current.MaxConcurrentDownloads || !_queue.TryDequeue(out next)) return;
                _running++;
            }

            if (next.Token.IsCancellationRequested)
            {
                next.MarkCancelled();
                lock (_lock) _running--;
                continue;
            }
            _ = Task.Run(() => ProcessAsync(next));
        }
    }

    private async Task ProcessAsync(DownloadJob job)
    {
        try
        {
            job.MarkStarted();
            await RunAsync(job);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.MarkCancelled();
        }
        catch (Exception ex)
        {
            if (ex is not LumaException) _logger.LogError(ex, "Download of {Key} failed", job.Wallpaper.Key);
            else _logger.LogWarning(ex, "Download of {Key} failed", job.Wallpaper.Key);
            job.MarkFailed(DownloadErrors.Describe(ex));
        }
        finally
        {
            lock (_lock) _running--;
            PumpQueue();
        }
    }

    private async Task RunAsync(DownloadJob job)
    {
        var ct = job.Token;
        var wallpaper = job.Wallpaper;

        var existing = await _library.FindByKeyAsync(wallpaper.Key, ct);
        if (existing is { FileExists: true })
        {
            job.MarkCompleted(existing, "Already in your library");
            return;
        }

        if (string.IsNullOrWhiteSpace(wallpaper.FullUrl) || !Uri.TryCreate(wallpaper.FullUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new DownloadException("This wallpaper doesn't have a downloadable file.");

        var directory = _settings.EffectiveDownloadDirectory;
        Directory.CreateDirectory(directory);
        EnsureDiskSpace(directory, wallpaper.FileSizeBytes);

        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.luma-part");
        try
        {
            string hash;
            long received;
            var client = _httpFactory.CreateClient(HttpClientName);
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    throw new DownloadException($"The server refused the download (HTTP {(int)response.StatusCode}).");

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)))
                    throw new DownloadException("The server returned a web page instead of an image.");

                var total = response.Content.Headers.ContentLength ?? wallpaper.FileSizeBytes;
                if (response.Content.Headers.ContentLength is { } length) EnsureDiskSpace(directory, length);

                using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await using var source = await response.Content.ReadAsStreamAsync(inactivity.Token);
                await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var buffer = new byte[81920];
                received = 0;
                var lastReport = Environment.TickCount64;
                while (true)
                {
                    inactivity.CancelAfter(InactivityTimeout);
                    var read = await source.ReadAsync(buffer, inactivity.Token);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    hasher.AppendData(buffer, 0, read);
                    received += read;

                    if (Environment.TickCount64 - lastReport >= 100)
                    {
                        job.ReportProgress(received, total);
                        lastReport = Environment.TickCount64;
                    }
                }

                if (response.Content.Headers.ContentLength is { } expected && expected != received)
                    throw new DownloadException("The download was interrupted before it finished.");

                job.ReportProgress(received, total ?? received);
                hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (received == 0) throw new DownloadException("The server sent an empty file.");

            var info = ImageInfoReader.TryRead(temp)
                       ?? throw new InvalidImageException("The downloaded file isn't a valid image (PNG, JPEG, WebP, GIF or BMP).");

            var duplicate = await _library.FindBySha256Async(hash, ct);
            if (duplicate is { FileExists: true })
            {
                job.MarkCompleted(duplicate, "An identical image is already in your library");
                return;
            }

            var finalPath = SafeFileName.BuildDownloadName(wallpaper, info.Extension, directory);
            File.Move(temp, finalPath);

            var stored = await _library.AddAsync(wallpaper with { Resolution = info.Resolution }, finalPath, hash, CancellationToken.None);
            job.MarkCompleted(stored);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void EnsureDiskSpace(string directory, long? requiredBytes)
    {
        if (requiredBytes is not > 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (root is null) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < requiredBytes.Value + 16L * 1024 * 1024)
                throw new DownloadException("There isn't enough free disk space for this download.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Free space could not be determined (e.g. network share); the actual write will report real problems.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort cleanup
        }
    }
}

public static class DownloadErrors
{
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;

    /// <summary>Turns any exception from the download pipeline into a short, non-technical sentence.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        LumaException lumaException => lumaException.Message,
        HttpRequestException => "Couldn't reach the server. Check your internet connection and try again.",
        UnauthorizedAccessException => "Luma doesn't have permission to write to the download folder.",
        DirectoryNotFoundException => "The download folder no longer exists. Choose another one in Settings.",
        IOException io when (io.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull => "There isn't enough free disk space for this download.",
        IOException => "The file couldn't be saved. The disk may be full, read-only or unavailable.",
        OperationCanceledException => "The server stopped responding, so the download was abandoned.",
        _ => "Something went wrong while downloading. Details were written to the log."
    };
}
