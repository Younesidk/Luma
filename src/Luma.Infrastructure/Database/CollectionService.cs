using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Luma.Infrastructure.Database;

public sealed class CollectionService(IDbContextFactory<LumaDbContext> factory) : ICollectionService
{
    public event EventHandler? CollectionsChanged;

    public async Task<IReadOnlyList<WallpaperCollection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var collections = await db.Collections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var counts = await db.CollectionWallpapers.AsNoTracking()
            .GroupBy(x => x.CollectionId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var result = new List<WallpaperCollection>(collections.Count);
        foreach (var c in collections)
        {
            var covers = await db.CollectionWallpapers.AsNoTracking()
                .Where(x => x.CollectionId == c.Id)
                .OrderByDescending(x => x.AddedAt)
                .Take(4)
                .Select(x => x.Wallpaper)
                .ToListAsync(cancellationToken);

            result.Add(new WallpaperCollection
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                CreatedAt = c.CreatedAt,
                WallpaperCount = counts.GetValueOrDefault(c.Id),
                CoverWallpapers = covers.Select(w => WallpaperMapper.ToModel(w)).ToList()
            });
        }
        return result;
    }

    public async Task<WallpaperCollection> CreateAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.Collections.AnyAsync(c => c.Name == name, cancellationToken))
            throw new LumaException($"A collection named \"{name}\" already exists.");

        var entity = new CollectionEntity
        {
            Name = name,
            Description = NormalizeDescription(description),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Collections.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
        return new WallpaperCollection { Id = entity.Id, Name = entity.Name, Description = entity.Description, CreatedAt = entity.CreatedAt };
    }

    public async Task RenameAsync(long collectionId, string name, string? description, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken)
                     ?? throw new LumaException("That collection no longer exists.");
        if (await db.Collections.AnyAsync(c => c.Id != collectionId && c.Name == name, cancellationToken))
            throw new LumaException($"A collection named \"{name}\" already exists.");

        entity.Name = name;
        entity.Description = NormalizeDescription(description);
        await db.SaveChangesAsync(cancellationToken);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(long collectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Cascade removes only the membership rows; wallpaper metadata and downloaded files are untouched.
        await db.Collections.Where(c => c.Id == collectionId).ExecuteDeleteAsync(cancellationToken);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddWallpaperAsync(long collectionId, Wallpaper wallpaper, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Collections.AnyAsync(c => c.Id == collectionId, cancellationToken))
            throw new LumaException("That collection no longer exists.");

        var entity = await WallpaperMapper.UpsertAsync(db, wallpaper, cancellationToken);
        var exists = await db.CollectionWallpapers.AnyAsync(x => x.CollectionId == collectionId && x.WallpaperId == entity.Id, cancellationToken);
        if (!exists)
        {
            db.CollectionWallpapers.Add(new CollectionWallpaperEntity
            {
                CollectionId = collectionId,
                WallpaperId = entity.Id,
                AddedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveWallpaperAsync(long collectionId, string wallpaperKey, CancellationToken cancellationToken = default)
    {
        var (provider, external) = SplitKey(wallpaperKey);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.CollectionWallpapers
            .Where(x => x.CollectionId == collectionId && x.Wallpaper.ProviderId == provider && x.Wallpaper.ExternalId == external)
            .ExecuteDeleteAsync(cancellationToken);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(long collectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CollectionWallpapers.AsNoTracking()
            .Where(x => x.CollectionId == collectionId)
            .OrderByDescending(x => x.AddedAt)
            .Select(x => x.Wallpaper)
            .ToListAsync(cancellationToken);
        return rows.Select(w => WallpaperMapper.ToModel(w)).ToList();
    }

    public async Task<IReadOnlySet<long>> GetCollectionIdsContainingAsync(string wallpaperKey, CancellationToken cancellationToken = default)
    {
        var (provider, external) = SplitKey(wallpaperKey);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ids = await db.CollectionWallpapers.AsNoTracking()
            .Where(x => x.Wallpaper.ProviderId == provider && x.Wallpaper.ExternalId == external)
            .Select(x => x.CollectionId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    private static string ValidateName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new LumaException("Give the collection a name.");
        if (name.Length > 120) throw new LumaException("Collection names can be at most 120 characters.");
        return name;
    }

    private static string? NormalizeDescription(string? description)
    {
        description = description?.Trim();
        return string.IsNullOrEmpty(description) ? null : description.Length > 500 ? description[..500] : description;
    }

    internal static (string Provider, string External) SplitKey(string key)
    {
        var index = key.IndexOf(':');
        return index < 0 ? (key, string.Empty) : (key[..index], key[(index + 1)..]);
    }
}
