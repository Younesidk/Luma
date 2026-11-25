using System.Security.Cryptography;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Videos;

/// <summary>
/// Imported video wallpapers. Every import is copied into &lt;data&gt;\Videos (named after its content hash, with the
/// extension that matches the real container) so the user can move or delete the original freely, and is verified by
/// opening it with the actual playback engine before it is accepted. Only files inside the managed folder are ever deleted.
/// </summary>
public sealed class VideoLibrary : IVideoLibrary
{
    private const string ImportingFolder = ".importing";
    private const string ThumbnailFolder = "Thumbnails";
    private const string PosterFolder = "Posters";
    private const long FreeSpaceMargin = 64L * 1024 * 1024;
    private const int HandleDiskFull = 0x27;
    private const int DiskFull = 0x70;

    private readonly IDbContextFactory<LumaDbContext> _factory;
    private readonly IVideoProbe _probe;
    private readonly IVideoOptimizer? _optimizer;
    private readonly ISettingsService? _settings;
    private readonly IDesktopVideoHost? _host;
    private readonly ILogger<VideoLibrary> _logger;
    private readonly SemaphoreSlim _importLock = new(1, 1);

    /// <param name="optimizer">With <paramref name="settings"/> and <paramref name="host"/>: shrinks imports much larger than the displays.</param>
    public VideoLibrary(IDbContextFactory<LumaDbContext> factory, IVideoProbe probe, ILumaPaths paths, ILogger<VideoLibrary> logger,
        IVideoOptimizer? optimizer = null, ISettingsService? settings = null, IDesktopVideoHost? host = null)
    {
        _factory = factory;
        _probe = probe;
        _optimizer = optimizer;
        _settings = settings;
        _host = host;
        _logger = logger;
        Directory = paths.VideosDirectory;
    }

    public event EventHandler? Changed;

    public string Directory { get; }

    public async Task<IReadOnlyList<VideoWallpaper>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.VideoWallpapers.AsNoTracking().Include(v => v.Wallpaper).ToListAsync(cancellationToken);
        return rows.OrderByDescending(r => r.ImportedAt).Select(Map).ToList();
    }

    public async Task<VideoWallpaper?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.VideoWallpapers.AsNoTracking().Include(v => v.Wallpaper).FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<VideoImportResult> ImportAsync(string sourcePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var container = VideoFileValidator.Validate(sourcePath);
        if (!_probe.Capabilities.IsSupported)
            throw new VideoNotPlayableException(_probe.Capabilities.Notes ?? "Video wallpapers aren't supported on this computer.");

        await _importLock.WaitAsync(cancellationToken);
        string? stagedFile = null;
        var createdFiles = new List<string>();
        try
        {
            var name = Path.GetFileName(sourcePath);
            var size = new FileInfo(sourcePath).Length;
            var hash = await HashAsync(sourcePath, size, progress, cancellationToken);

            var existing = await FindByHashAsync(hash, cancellationToken);
            if (existing is not null && existing.FileExists)
            {
                progress?.Report(1);
                return new VideoImportResult(existing, AlreadyExisted: true);
            }

            System.IO.Directory.CreateDirectory(Directory);
            EnsureFreeSpace(size, name);

            var baseName = hash[..20];
            var finalPath = Path.Combine(Directory, baseName + VideoFileValidator.CanonicalExtension(container));
            stagedFile = await CopyToStagingAsync(sourcePath, size, progress, cancellationToken);
            File.Move(stagedFile, finalPath, overwrite: true);
            stagedFile = null;
            createdFiles.Add(finalPath);

            VideoProbeResult probe;
            try
            {
                probe = await _probe.ProbeAsync(finalPath, cancellationToken);
            }
            catch (VideoNotPlayableException ex)
            {
                throw new VideoNotPlayableException($"\"{name}\" can't be played: {ex.Message}", ex);
            }

            WallpaperResolution? resizedFrom = null;
            if (ShrinkTarget(probe) is { } target)
            {
                var shrunk = await ShrinkAsync(finalPath, baseName, probe, target, progress, cancellationToken);
                if (shrunk is not null)
                {
                    resizedFrom = new WallpaperResolution(probe.Width, probe.Height);
                    createdFiles.Remove(finalPath);
                    if (!string.Equals(shrunk.Value.Path, finalPath, StringComparison.OrdinalIgnoreCase)) TryDelete(finalPath);
                    finalPath = shrunk.Value.Path;
                    createdFiles.Add(finalPath);
                    probe = shrunk.Value.Probe;
                    container = VideoContainer.Mp4;
                    size = new FileInfo(finalPath).Length;
                }
            }

            var thumbnail = Path.Combine(Directory, ThumbnailFolder, baseName + ".png");
            var poster = Path.Combine(Directory, PosterFolder, baseName + ".png");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(poster)!);
            await File.WriteAllBytesAsync(thumbnail, probe.ThumbnailPng, cancellationToken);
            createdFiles.Add(thumbnail);
            await File.WriteAllBytesAsync(poster, probe.PosterPng, cancellationToken);
            createdFiles.Add(poster);

            var info = new VideoInfo(probe.Width, probe.Height, probe.Duration, container, probe.HasAudio);
            var stored = existing is null
                ? await InsertAsync(sourcePath, finalPath, hash, size, info, thumbnail, poster, cancellationToken)
                : await RepairAsync(existing.Id, sourcePath, finalPath, size, info, thumbnail, poster, cancellationToken);
            createdFiles.Clear(); // committed: the files now belong to the entry
            progress?.Report(1);
            Changed?.Invoke(this, EventArgs.Empty);
            return new VideoImportResult(stored, AlreadyExisted: false, resizedFrom);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is DiskFull or HandleDiskFull)
        {
            throw new VideoNotPlayableException($"There isn't enough free disk space to import \"{Path.GetFileName(sourcePath)}\".", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Importing video {Path} failed", sourcePath);
            throw new VideoNotPlayableException(
                $"\"{Path.GetFileName(sourcePath)}\" couldn't be copied into Luma's video folder. It may be in use, or the disk may be protected.", ex);
        }
        finally
        {
            if (stagedFile is not null) TryDelete(stagedFile);
            foreach (var file in createdFiles) TryDelete(file);
            _importLock.Release();
        }
    }

    /// <summary>The size to shrink to, or null when the setting is off, shrinking isn't available or the video is small enough.</summary>
    private (int Width, int Height)? ShrinkTarget(VideoProbeResult probe)
    {
        if (_optimizer is not { IsSupported: true } || _settings?.Current.VideoShrinkOnImport != true || _host is null) return null;
        try
        {
            return VideoShrinkPolicy.TargetSize(probe.Width, probe.Height, _host.GetDisplays());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The displays couldn't be read; the video is imported at its own size");
            return null;
        }
    }

    /// <summary>
    /// Re-encodes the managed copy at screen size. Returns the new file and its probe, or null to keep the original copy
    /// (the import never fails because of this optional step).
    /// </summary>
    private async Task<(string Path, VideoProbeResult Probe)?> ShrinkAsync(string copy, string baseName, VideoProbeResult original,
        (int Width, int Height) target, IProgress<double>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(Directory, ImportingFolder, Guid.NewGuid().ToString("N") + ".mp4");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        try
        {
            // Forward synchronously: Progress<T> posts each report to the thread pool, so a late 0.945 could arrive
            // after 0.99 or 1 and make the import bar jump backwards. The caller's own IProgress handles marshalling.
            var step = progress is null ? null : new ForwardingProgress(p => progress.Report(0.9 + 0.09 * p));
            await _optimizer!.ShrinkAsync(copy, staging, target.Width, target.Height, original.Duration, step, ct);
            var probe = await _probe.ProbeAsync(staging, ct);
            var path = Path.Combine(Directory, baseName + ".mp4");
            File.Move(staging, path, overwrite: true); // replaces the copy when it was already an .mp4
            _logger.LogInformation("Shrunk {Name} from {OldWidth}x{OldHeight} to {Width}x{Height} for the displays",
                baseName, original.Width, original.Height, probe.Width, probe.Height);
            return (path, probe);
        }
        catch (Exception ex) when (ex is VideoNotPlayableException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Shrinking {Name} failed; keeping the original size", baseName);
            return null;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var row = await db.VideoWallpapers.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (row is null) return;

        var files = new[] { row.FilePath, row.ThumbnailPath, row.PosterPath };
        db.VideoWallpapers.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var file in files)
        {
            // Safety net: a path pointing outside the managed folder (hand-edited database...) is never deleted.
            if (file is not null && IsManaged(file)) TryDelete(file);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(Directory)) return;

        var staging = Path.Combine(Directory, ImportingFolder);
        if (System.IO.Directory.Exists(staging))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(staging)) TryDelete(file);
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await db.VideoWallpapers.AsNoTracking().ToListAsync(cancellationToken))
        {
            referenced.Add(Path.GetFullPath(row.FilePath));
            if (row.ThumbnailPath is not null) referenced.Add(Path.GetFullPath(row.ThumbnailPath));
            if (row.PosterPath is not null) referenced.Add(Path.GetFullPath(row.PosterPath));
        }

        // Only files Luma itself names (20 hex characters) are considered, so nothing the user put here is touched.
        IEnumerable<string> Candidates(string folder, params string[] extensions) =>
            System.IO.Directory.Exists(folder)
                ? System.IO.Directory.EnumerateFiles(folder).Where(f =>
                    extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                    && Path.GetFileNameWithoutExtension(f) is { Length: 20 } stem && stem.All(Uri.IsHexDigit))
                : [];

        var orphans = Candidates(Directory, ".mp4", ".mov", ".webm")
            .Concat(Candidates(Path.Combine(Directory, ThumbnailFolder), ".png"))
            .Concat(Candidates(Path.Combine(Directory, PosterFolder), ".png"))
            .Where(f => !referenced.Contains(Path.GetFullPath(f)))
            .ToList();
        foreach (var orphan in orphans)
        {
            _logger.LogInformation("Removing unreferenced video file {Path}", orphan);
            TryDelete(orphan);
        }
    }

    private async Task<VideoWallpaper?> FindByHashAsync(string hash, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.VideoWallpapers.AsNoTracking().Include(v => v.Wallpaper).FirstOrDefaultAsync(v => v.Sha256 == hash, ct);
        return row is null ? null : Map(row);
    }

    private async Task<VideoWallpaper> InsertAsync(string source, string path, string hash, long size, VideoInfo info,
        string thumbnail, string poster, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wallpaper = new Wallpaper
        {
            ProviderId = Wallpaper.LocalProviderId,
            ExternalId = "video-" + hash[..20],
            Title = Path.GetFileNameWithoutExtension(source),
            ThumbnailUrl = thumbnail,
            Resolution = new WallpaperResolution(info.Width, info.Height),
            FileSizeBytes = size,
            FileType = VideoFileValidator.MimeType(info.Container),
            LocalFilePath = path
        };
        var wallpaperRow = await WallpaperMapper.UpsertAsync(db, wallpaper, ct);
        var row = new VideoWallpaperEntity
        {
            WallpaperId = wallpaperRow.Id,
            Wallpaper = wallpaperRow,
            FilePath = path,
            OriginalPath = source,
            Sha256 = hash,
            FileSizeBytes = size,
            Width = info.Width,
            Height = info.Height,
            DurationMilliseconds = (long)info.Duration.TotalMilliseconds,
            Container = info.Container.ToString(),
            HasAudio = info.HasAudio,
            ThumbnailPath = thumbnail,
            PosterPath = poster,
            ImportedAt = DateTimeOffset.UtcNow
        };
        db.VideoWallpapers.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    /// <summary>The entry existed but its managed file had vanished: the re-imported copy takes its place.</summary>
    private async Task<VideoWallpaper> RepairAsync(long id, string source, string path, long size, VideoInfo info,
        string thumbnail, string poster, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.VideoWallpapers.Include(v => v.Wallpaper).FirstAsync(v => v.Id == id, ct);
        row.FilePath = path;
        row.OriginalPath = source;
        row.FileSizeBytes = size;
        row.Width = info.Width;
        row.Height = info.Height;
        row.DurationMilliseconds = (long)info.Duration.TotalMilliseconds;
        row.Container = info.Container.ToString();
        row.HasAudio = info.HasAudio;
        row.ThumbnailPath = thumbnail;
        row.PosterPath = poster;
        row.Wallpaper.ThumbnailUrl = thumbnail;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Video {Id} was missing its file and has been restored from {Source}", id, source);
        return Map(row);
    }

    private static async Task<string> HashAsync(string path, long size, IProgress<double>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(size > 0 ? 0.45 * done / size : 0);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<string> CopyToStagingAsync(string source, long size, IProgress<double>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(Directory, ImportingFolder);
        System.IO.Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(0.45 + (size > 0 ? 0.45 * done / size : 0));
            }
            await output.FlushAsync(ct);
            if (output.Length != size) throw new IOException("The copied video is incomplete.");
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    private void EnsureFreeSpace(long size, string name)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Directory));
            if (root is null) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < size + FreeSpaceMargin)
                throw new VideoNotPlayableException($"There isn't enough free disk space to import \"{name}\".");
        }
        catch (ArgumentException)
        {
            // Network or unusual paths: let the copy itself report problems.
        }
    }

    private bool IsManaged(string path)
    {
        var root = Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usually a file still opened by the player; CleanupAsync removes it on the next start.
            _logger.LogWarning(ex, "Could not delete {Path}; it will be cleaned up later", path);
        }
    }

    private static VideoWallpaper Map(VideoWallpaperEntity row) => new()
    {
        Id = row.Id,
        Wallpaper = WallpaperMapper.ToModel(row.Wallpaper, row.FilePath),
        FilePath = row.FilePath,
        OriginalPath = row.OriginalPath,
        Sha256 = row.Sha256,
        FileSizeBytes = row.FileSizeBytes,
        Info = new VideoInfo(row.Width, row.Height, TimeSpan.FromMilliseconds(row.DurationMilliseconds),
            Enum.TryParse<VideoContainer>(row.Container, out var container) ? container : VideoContainer.Mp4, row.HasAudio),
        ThumbnailPath = row.ThumbnailPath,
        PosterPath = row.PosterPath,
        ImportedAt = row.ImportedAt,
        FileExists = File.Exists(row.FilePath)
    };

    private sealed class ForwardingProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
