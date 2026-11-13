using System.Collections.Concurrent;
using System.Security.Cryptography;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Database;

public sealed class DownloadLibrary(IDbContextFactory<LumaDbContext> factory, ILogger<DownloadLibrary> logger) : IDownloadLibrary
{
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.Ordinal);

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Downloads.AsNoTracking()
            .Select(d => new { d.Wallpaper.ProviderId, d.Wallpaper.ExternalId, d.FilePath })
            .ToListAsync(cancellationToken);
        _paths.Clear();
        foreach (var row in rows) _paths[Wallpaper.MakeKey(row.ProviderId, row.ExternalId)] = row.FilePath;
    }

    public string? GetLocalPath(string wallpaperKey) =>
        _paths.TryGetValue(wallpaperKey, out var path) && File.Exists(path) ? path : null;

    public bool IsDownloaded(string wallpaperKey) => GetLocalPath(wallpaperKey) is not null;

    public async Task<IReadOnlyList<DownloadedWallpaper>> QueryAsync(LibraryQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var q = db.Downloads.AsNoTracking().Include(d => d.Wallpaper).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.ProviderId)) q = q.Where(d => d.Wallpaper.ProviderId == query.ProviderId);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var pattern = QueryHelpers.ContainsPattern(query.Text.Trim());
            q = q.Where(d => EF.Functions.Like(d.Wallpaper.Title ?? "", pattern, "\\")
                             || EF.Functions.Like(d.Wallpaper.CreatorName ?? "", pattern, "\\")
                             || EF.Functions.Like(d.FilePath, pattern, "\\"));
        }

        var rows = await q.ToListAsync(cancellationToken);
        return QueryHelpers.Sort(rows, query.Sort, r => r.DownloadedAt, r => r.Wallpaper.Title ?? Path.GetFileName(r.FilePath),
                r => (long)(r.Wallpaper.Width ?? 0) * (r.Wallpaper.Height ?? 0), r => r.FileSizeBytes)
            .Select(Map)
            .ToList();
    }

    public async Task<DownloadedWallpaper?> FindByKeyAsync(string wallpaperKey, CancellationToken cancellationToken = default)
    {
        var (provider, external) = CollectionService.SplitKey(wallpaperKey);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Downloads.AsNoTracking().Include(d => d.Wallpaper)
            .FirstOrDefaultAsync(d => d.Wallpaper.ProviderId == provider && d.Wallpaper.ExternalId == external, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<DownloadedWallpaper?> FindBySha256Async(string sha256, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Downloads.AsNoTracking().Include(d => d.Wallpaper)
            .FirstOrDefaultAsync(d => d.Sha256 == sha256, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<DownloadedWallpaper> AddAsync(Wallpaper wallpaper, string filePath, string? sha256, CancellationToken cancellationToken = default)
    {
        var size = new FileInfo(filePath).Length;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var info = ImageInfoReader.TryRead(filePath);
        var withActualData = wallpaper with
        {
            Resolution = info?.Resolution ?? wallpaper.Resolution,
            FileSizeBytes = size,
            FileType = info?.MimeType ?? wallpaper.FileType
        };
        var entity = await WallpaperMapper.UpsertAsync(db, withActualData, cancellationToken);

        var existing = await db.Downloads.Include(d => d.Wallpaper).FirstOrDefaultAsync(d => d.WallpaperId == entity.Id, cancellationToken);
        if (existing is null)
        {
            existing = new DownloadEntity { WallpaperId = entity.Id, Wallpaper = entity };
            db.Downloads.Add(existing);
        }
        existing.FilePath = filePath;
        existing.FileSizeBytes = size;
        existing.Sha256 = sha256;
        existing.DownloadedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _paths[withActualData.Key] = filePath;
        Changed?.Invoke(this, new LibraryChangedEventArgs(withActualData.Key, true));
        return Map(existing);
    }

    public async Task<DownloadedWallpaper> ImportFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) throw new LumaException("That file no longer exists.");
        var info = ImageInfoReader.TryRead(filePath)
                   ?? throw new InvalidImageException($"\"{Path.GetFileName(filePath)}\" is not a supported image (PNG, JPEG, WebP, GIF or BMP).");

        string hash;
        await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        var duplicate = await FindBySha256Async(hash, cancellationToken);
        if (duplicate is not null) return duplicate;

        var wallpaper = new Wallpaper
        {
            ProviderId = Wallpaper.LocalProviderId,
            ExternalId = hash[..20],
            Title = Path.GetFileNameWithoutExtension(filePath),
            Resolution = info.Resolution,
            FileType = info.MimeType,
            FileSizeBytes = new FileInfo(filePath).Length,
            LocalFilePath = filePath
        };
        return await AddAsync(wallpaper, filePath, hash, cancellationToken);
    }

    public async Task RemoveAsync(long downloadId, bool deleteFile, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Downloads.Include(d => d.Wallpaper).FirstOrDefaultAsync(d => d.Id == downloadId, cancellationToken);
        if (row is null) return;

        var key = Wallpaper.MakeKey(row.Wallpaper.ProviderId, row.Wallpaper.ExternalId);
        var path = row.FilePath;
        db.Downloads.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        _paths.TryRemove(key, out _);

        if (deleteFile)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Library entry removed but the file could not be deleted: {Path}", path);
                Changed?.Invoke(this, new LibraryChangedEventArgs(key, false));
                throw new LumaException("The library entry was removed, but the file could not be deleted. It may be in use or protected.", ex);
            }
        }
        Changed?.Invoke(this, new LibraryChangedEventArgs(key, false));
    }

    public async Task<IReadOnlyList<DownloadedWallpaper>> GetRotationCandidatesAsync(RotationSource source, long? collectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var q = db.Downloads.AsNoTracking().Include(d => d.Wallpaper).AsQueryable();
        switch (source)
        {
            case RotationSource.Favorites:
                q = q.Where(d => db.Favorites.Any(f => f.WallpaperId == d.WallpaperId));
                break;
            case RotationSource.Collection when collectionId is not null:
                var id = collectionId.Value;
                q = q.Where(d => db.CollectionWallpapers.Any(c => c.CollectionId == id && c.WallpaperId == d.WallpaperId));
                break;
            case RotationSource.Collection:
                return [];
        }

        var rows = await q.ToListAsync(cancellationToken);
        return rows.Select(Map).Where(d => d.FileExists).ToList();
    }

    private static DownloadedWallpaper Map(DownloadEntity row) => new()
    {
        Id = row.Id,
        Wallpaper = WallpaperMapper.ToModel(row.Wallpaper, row.FilePath),
        FilePath = row.FilePath,
        FileSizeBytes = row.FileSizeBytes,
        Sha256 = row.Sha256,
        DownloadedAt = row.DownloadedAt,
        FileExists = File.Exists(row.FilePath)
    };
}
