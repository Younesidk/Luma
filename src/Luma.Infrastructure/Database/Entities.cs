namespace Luma.Infrastructure.Database;

/// <summary>Locally stored metadata of a wallpaper (never the image bytes).</summary>
public class WallpaperEntity
{
    public long Id { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? CreatorName { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? PreviewUrl { get; set; }
    public string? FullUrl { get; set; }
    public string? SourcePageUrl { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileType { get; set; }
    public DateTimeOffset? SourceCreatedAt { get; set; }

    /// <summary>Remaining optional metadata (license, tags, colours...) serialised as JSON.</summary>
    public string? MetadataJson { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

public class FavoriteEntity
{
    public long WallpaperId { get; set; }
    public WallpaperEntity Wallpaper { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public class CollectionEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<CollectionWallpaperEntity> Items { get; set; } = [];
}

public class CollectionWallpaperEntity
{
    public long CollectionId { get; set; }
    public CollectionEntity Collection { get; set; } = null!;
    public long WallpaperId { get; set; }
    public WallpaperEntity Wallpaper { get; set; } = null!;
    public DateTimeOffset AddedAt { get; set; }
}

public class DownloadEntity
{
    public long Id { get; set; }
    public long WallpaperId { get; set; }
    public WallpaperEntity Wallpaper { get; set; } = null!;
    public string FilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset DownloadedAt { get; set; }
}

public class WallpaperHistoryEntity
{
    public long Id { get; set; }
    public long WallpaperId { get; set; }
    public WallpaperEntity Wallpaper { get; set; } = null!;
    public DateTimeOffset AppliedAt { get; set; }
    public string? FilePath { get; set; }
    public int FitMode { get; set; }
}

public class AppSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

/// <summary>An imported video wallpaper. The file lives in Luma's managed Videos folder.</summary>
public class VideoWallpaperEntity
{
    public long Id { get; set; }
    public long WallpaperId { get; set; }
    public WallpaperEntity Wallpaper { get; set; } = null!;
    public string FilePath { get; set; } = string.Empty;
    public string? OriginalPath { get; set; }
    /// <summary>Content hash of the imported file.</summary>
    public string Sha256 { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long DurationMilliseconds { get; set; }
    public string Container { get; set; } = string.Empty;
    public bool HasAudio { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? PosterPath { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}
