using System.Collections.Concurrent;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Luma.Infrastructure.Database;

internal static class QueryHelpers
{
    /// <summary>Builds an escaped LIKE pattern ("%text%") for case-insensitive contains searches.</summary>
    public static string ContainsPattern(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static IOrderedEnumerable<T> Sort<T>(IEnumerable<T> source, LibrarySort sort, Func<T, DateTimeOffset> date,
        Func<T, string> name, Func<T, long> pixels, Func<T, long> size) => sort switch
    {
        LibrarySort.Oldest => source.OrderBy(date),
        LibrarySort.Name => source.OrderBy(name, StringComparer.CurrentCultureIgnoreCase),
        LibrarySort.Resolution => source.OrderByDescending(pixels).ThenByDescending(date),
        LibrarySort.FileSize => source.OrderByDescending(size).ThenByDescending(date),
        _ => source.OrderByDescending(date)
    };
}

public sealed class FavoritesService(IDbContextFactory<LumaDbContext> factory) : IFavoritesService
{
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    public event EventHandler<FavoriteChangedEventArgs>? FavoriteChanged;

    public int Count => _keys.Count;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Favorites.AsNoTracking()
            .Select(f => new { f.Wallpaper.ProviderId, f.Wallpaper.ExternalId })
            .ToListAsync(cancellationToken);
        _keys.Clear();
        foreach (var row in rows) _keys[Wallpaper.MakeKey(row.ProviderId, row.ExternalId)] = 0;
    }

    public bool IsFavorite(string key) => _keys.ContainsKey(key);

    public async Task SetFavoriteAsync(Wallpaper wallpaper, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var entity = await WallpaperMapper.UpsertAsync(db, wallpaper, cancellationToken);
        var existing = await db.Favorites.FirstOrDefaultAsync(f => f.WallpaperId == entity.Id, cancellationToken);

        if (isFavorite && existing is null)
        {
            db.Favorites.Add(new FavoriteEntity { WallpaperId = entity.Id, CreatedAt = DateTimeOffset.UtcNow });
        }
        else if (!isFavorite && existing is not null)
        {
            db.Favorites.Remove(existing);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var changed = isFavorite ? _keys.TryAdd(wallpaper.Key, 0) : _keys.TryRemove(wallpaper.Key, out _);
        if (changed) FavoriteChanged?.Invoke(this, new FavoriteChangedEventArgs(wallpaper.Key, isFavorite));
    }

    public async Task<bool> ToggleAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default)
    {
        var next = !IsFavorite(wallpaper.Key);
        await SetFavoriteAsync(wallpaper, next, cancellationToken);
        return next;
    }

    public async Task<IReadOnlyList<Wallpaper>> QueryAsync(LibraryQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var q = db.Favorites.AsNoTracking().Include(f => f.Wallpaper).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.ProviderId)) q = q.Where(f => f.Wallpaper.ProviderId == query.ProviderId);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var pattern = QueryHelpers.ContainsPattern(query.Text.Trim());
            q = q.Where(f => EF.Functions.Like(f.Wallpaper.Title ?? "", pattern, "\\")
                             || EF.Functions.Like(f.Wallpaper.CreatorName ?? "", pattern, "\\")
                             || EF.Functions.Like(f.Wallpaper.ExternalId, pattern, "\\"));
        }

        var rows = await q.ToListAsync(cancellationToken);
        return QueryHelpers.Sort(rows, query.Sort, r => r.CreatedAt, r => r.Wallpaper.Title ?? r.Wallpaper.ExternalId,
                r => (long)(r.Wallpaper.Width ?? 0) * (r.Wallpaper.Height ?? 0), r => r.Wallpaper.FileSizeBytes ?? 0)
            .Select(r => WallpaperMapper.ToModel(r.Wallpaper))
            .ToList();
    }
}
