using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Luma.Infrastructure.Database;

public sealed class HistoryService(IDbContextFactory<LumaDbContext> factory) : IHistoryService
{
    private const int MaxEntries = 500;

    public event EventHandler? Changed;

    public async Task RecordAsync(Wallpaper wallpaper, string? filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var entity = await WallpaperMapper.UpsertAsync(db, wallpaper, cancellationToken);

        var last = await db.History.OrderByDescending(h => h.AppliedAt).ThenByDescending(h => h.Id).FirstOrDefaultAsync(cancellationToken);
        if (last is not null && last.WallpaperId == entity.Id)
        {
            // Re-applying the wallpaper that is already active: refresh instead of duplicating.
            last.AppliedAt = DateTimeOffset.UtcNow;
            last.FilePath = filePath;
            last.FitMode = (int)fitMode;
        }
        else
        {
            db.History.Add(new WallpaperHistoryEntity
            {
                WallpaperId = entity.Id,
                AppliedAt = DateTimeOffset.UtcNow,
                FilePath = filePath,
                FitMode = (int)fitMode
            });
        }
        await db.SaveChangesAsync(cancellationToken);

        var total = await db.History.CountAsync(cancellationToken);
        if (total > MaxEntries)
        {
            var cutoff = await db.History.OrderByDescending(h => h.AppliedAt).Skip(MaxEntries - 1).Select(h => h.AppliedAt).FirstAsync(cancellationToken);
            await db.History.Where(h => h.AppliedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.History.AsNoTracking().Include(h => h.Wallpaper)
            .OrderByDescending(h => h.AppliedAt).ThenByDescending(h => h.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(h => new HistoryEntry
        {
            Id = h.Id,
            Wallpaper = WallpaperMapper.ToModel(h.Wallpaper, h.FilePath),
            AppliedAt = h.AppliedAt,
            FilePath = h.FilePath,
            FitMode = (WallpaperFitMode)h.FitMode
        }).ToList();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.History.ExecuteDeleteAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class StateStore(IDbContextFactory<LumaDbContext> factory) : IStateStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null) db.AppSettings.Add(new AppSettingEntity { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(cancellationToken);
    }
}
