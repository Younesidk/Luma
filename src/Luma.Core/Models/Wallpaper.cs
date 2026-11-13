namespace Luma.Core.Models;

/// <summary>
/// Provider-neutral wallpaper description. Every provider maps its own API payload into this model so
/// that the UI and database never depend on provider specific types. Optional fields may be missing.
/// </summary>
public sealed record Wallpaper
{
    public const string LocalProviderId = "local";

    /// <summary>Identifier of the provider the wallpaper originates from (e.g. "wallhaven").</summary>
    public required string ProviderId { get; init; }

    /// <summary>Identifier of the wallpaper within its provider.</summary>
    public required string ExternalId { get; init; }

    public string? Title { get; init; }

    /// <summary>Small image suitable for gallery grids.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>Larger image suitable for the preview (never a multi-megabyte original).</summary>
    public string? PreviewUrl { get; init; }

    /// <summary>Original, full resolution file.</summary>
    public string? FullUrl { get; init; }

    /// <summary>Page of the provider where this wallpaper lives.</summary>
    public string? SourcePageUrl { get; init; }

    public WallpaperResolution? Resolution { get; init; }

    public long? FileSizeBytes { get; init; }

    /// <summary>MIME type or extension when known (e.g. "image/jpeg").</summary>
    public string? FileType { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public WallpaperMetadata Metadata { get; init; } = WallpaperMetadata.Empty;

    /// <summary>Set when the wallpaper exists on disk inside the local library.</summary>
    public string? LocalFilePath { get; init; }

    /// <summary>Stable key that is unique across providers.</summary>
    public string Key => MakeKey(ProviderId, ExternalId);

    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title!
        : ProviderId == LocalProviderId && !string.IsNullOrWhiteSpace(LocalFilePath) ? Path.GetFileNameWithoutExtension(LocalFilePath)
        : $"{char.ToUpperInvariant(ProviderId[0])}{ProviderId[1..]} {ExternalId}";

    public static string MakeKey(string providerId, string externalId) => $"{providerId}:{externalId}";
}

/// <summary>Optional descriptive information. Providers fill what they know and leave the rest empty.</summary>
public sealed record WallpaperMetadata
{
    public static WallpaperMetadata Empty { get; } = new();

    public string? Description { get; init; }
    public string? CreatorName { get; init; }
    public string? CreatorUrl { get; init; }

    /// <summary>License short name such as "CC BY 2.0".</summary>
    public string? LicenseName { get; init; }
    public string? LicenseUrl { get; init; }

    /// <summary>Ready-to-display attribution line supplied by the provider.</summary>
    public string? Attribution { get; init; }

    /// <summary>Upstream source that hosts the original (e.g. "flickr" for Openverse results).</summary>
    public string? UpstreamSource { get; init; }

    public string? Category { get; init; }
    public string? Purity { get; init; }
    public int? ViewCount { get; init; }
    public int? FavoriteCount { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Dominant colours as "#rrggbb" values.</summary>
    public IReadOnlyList<string> Colors { get; init; } = [];

    public bool RequiresAttribution => !string.IsNullOrWhiteSpace(LicenseName) || !string.IsNullOrWhiteSpace(Attribution);
}
