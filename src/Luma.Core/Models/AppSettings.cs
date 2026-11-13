using Luma.Core.Enums;

namespace Luma.Core.Models;

/// <summary>User configurable settings persisted to settings.json.</summary>
public sealed class AppSettings
{
    public const string AllSourcesId = "all";

    // Appearance
    public ThemePreference Theme { get; set; } = ThemePreference.Dark;
    public string AccentPreset { get; set; } = "violet";
    public GalleryDensity Density { get; set; } = GalleryDensity.Comfortable;
    public bool SidebarCompact { get; set; }

    /// <summary>Takes effect on the next start (the renderer is chosen before any window exists).</summary>
    public RenderingMode RenderingMode { get; set; } = RenderingMode.Efficient;

    // Downloads
    /// <summary>Custom download directory. Null/empty means the default location inside Luma's data directory.</summary>
    public string? DownloadDirectory { get; set; }
    public bool ConfirmBeforeDelete { get; set; } = true;

    // Providers
    public bool WallhavenEnabled { get; set; } = true;
    public string? WallhavenApiKey { get; set; }
    public bool OpenverseEnabled { get; set; } = true;
    public string? OpenverseClientId { get; set; }
    public string? OpenverseClientSecret { get; set; }

    // Wallpaper
    public WallpaperFitMode DefaultFitMode { get; set; } = WallpaperFitMode.Fill;
    /// <summary>Provider id shown on start-up ("all" for every enabled provider).</summary>
    public string DefaultSource { get; set; } = "wallhaven";

    // Rotation
    public bool RotationEnabled { get; set; }
    public int RotationIntervalMinutes { get; set; } = 60;
    public RotationSource RotationSource { get; set; } = RotationSource.AllDownloads;
    public long? RotationCollectionId { get; set; }
    public bool RotationRandom { get; set; } = true;

    // Performance
    public int ThumbnailCacheMegabytes { get; set; } = 256;
    public int MemoryCacheItems { get; set; } = 80;
    public int MaxConcurrentDownloads { get; set; } = 3;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Clamps values to sane ranges so a hand-edited settings file can never break the app.</summary>
    public void Normalize()
    {
        ThumbnailCacheMegabytes = Math.Clamp(ThumbnailCacheMegabytes, 32, 4096);
        MemoryCacheItems = Math.Clamp(MemoryCacheItems, 30, 600);
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 8);
        RotationIntervalMinutes = Math.Clamp(RotationIntervalMinutes, 1, 60 * 24 * 14);
        if (string.IsNullOrWhiteSpace(DefaultSource)) DefaultSource = AllSourcesId;
        if (string.IsNullOrWhiteSpace(AccentPreset)) AccentPreset = "violet";
    }
}
