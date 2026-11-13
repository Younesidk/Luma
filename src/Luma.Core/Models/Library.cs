using Luma.Core.Enums;

namespace Luma.Core.Models;

public sealed record WallpaperCollection
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int WallpaperCount { get; init; }

    /// <summary>Wallpapers used for the cover mosaic (up to four).</summary>
    public IReadOnlyList<Wallpaper> CoverWallpapers { get; init; } = [];
}

public sealed record DownloadedWallpaper
{
    public long Id { get; init; }
    public required Wallpaper Wallpaper { get; init; }
    public required string FilePath { get; init; }
    public long FileSizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public DateTimeOffset DownloadedAt { get; init; }
    public bool FileExists { get; init; } = true;
}

public sealed record HistoryEntry
{
    public long Id { get; init; }
    public required Wallpaper Wallpaper { get; init; }
    public DateTimeOffset AppliedAt { get; init; }
    public string? FilePath { get; init; }
    public WallpaperFitMode FitMode { get; init; }
}

public sealed record LibraryQuery
{
    public string? Text { get; init; }
    public LibrarySort Sort { get; init; } = LibrarySort.Newest;
    public string? ProviderId { get; init; }
}

public sealed record ApplyResult(bool Success, ApplyFailureReason Reason = ApplyFailureReason.None, string? Message = null)
{
    public static ApplyResult Ok(string? message = null) => new(true, ApplyFailureReason.None, message);

    public static ApplyResult Fail(ApplyFailureReason reason, string message) => new(false, reason, message);
}

public sealed record WallpaperApplierCapabilities(
    bool IsSupported,
    string PlatformName,
    IReadOnlyList<WallpaperFitMode> SupportedFitModes,
    string? Notes = null);
