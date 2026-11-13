using System.Text.Json;
using Luma.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Luma.Infrastructure.Database;

/// <summary>Converts between the provider-neutral <see cref="Wallpaper"/> and its database row.</summary>
internal static class WallpaperMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static Wallpaper ToModel(WallpaperEntity e, string? localFilePath = null)
    {
        WallpaperMetadata metadata = WallpaperMetadata.Empty;
        if (!string.IsNullOrEmpty(e.MetadataJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<WallpaperMetadata>(e.MetadataJson, Json) ?? WallpaperMetadata.Empty;
            }
            catch (JsonException)
            {
                // Corrupt metadata must never prevent the wallpaper from being listed.
            }
        }

        return new Wallpaper
        {
            ProviderId = e.ProviderId,
            ExternalId = e.ExternalId,
            Title = e.Title,
            ThumbnailUrl = e.ThumbnailUrl,
            PreviewUrl = e.PreviewUrl,
            FullUrl = e.FullUrl,
            SourcePageUrl = e.SourcePageUrl,
            Resolution = e.Width is > 0 && e.Height is > 0 ? new WallpaperResolution(e.Width.Value, e.Height.Value) : null,
            FileSizeBytes = e.FileSizeBytes,
            FileType = e.FileType,
            CreatedAt = e.SourceCreatedAt,
            Metadata = metadata,
            LocalFilePath = localFilePath
        };
    }

    public static void Apply(Wallpaper w, WallpaperEntity e)
    {
        e.ProviderId = w.ProviderId;
        e.ExternalId = w.ExternalId;
        e.Title = w.Title;
        e.CreatorName = w.Metadata.CreatorName;
        e.ThumbnailUrl = w.ThumbnailUrl;
        e.PreviewUrl = w.PreviewUrl;
        e.FullUrl = w.FullUrl;
        e.SourcePageUrl = w.SourcePageUrl;
        e.Width = w.Resolution?.Width;
        e.Height = w.Resolution?.Height;
        e.FileSizeBytes = w.FileSizeBytes;
        e.FileType = w.FileType;
        e.SourceCreatedAt = w.CreatedAt;
        e.MetadataJson = JsonSerializer.Serialize(w.Metadata, Json);
    }

    /// <summary>Inserts or refreshes the wallpaper row and returns it (tracked). Must be called inside a context scope.</summary>
    public static async Task<WallpaperEntity> UpsertAsync(LumaDbContext db, Wallpaper wallpaper, CancellationToken ct)
    {
        var existing = await db.Wallpapers.FirstOrDefaultAsync(
            x => x.ProviderId == wallpaper.ProviderId && x.ExternalId == wallpaper.ExternalId, ct);
        if (existing is null)
        {
            existing = new WallpaperEntity { AddedAt = DateTimeOffset.UtcNow };
            Apply(wallpaper, existing);
            db.Wallpapers.Add(existing);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // Keep richer stored data when the incoming copy is sparser (e.g. a list result vs. a detail result).
            if (HasLessInformation(wallpaper, existing)) return existing;
            Apply(wallpaper, existing);
            await db.SaveChangesAsync(ct);
        }
        return existing;
    }

    private static bool HasLessInformation(Wallpaper incoming, WallpaperEntity existing) =>
        string.IsNullOrEmpty(incoming.ThumbnailUrl) && !string.IsNullOrEmpty(existing.ThumbnailUrl);
}
