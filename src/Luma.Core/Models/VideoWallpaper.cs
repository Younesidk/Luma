using Luma.Core.Enums;

namespace Luma.Core.Models;

/// <summary>
/// A video stored in Luma's managed video folder. <see cref="Wallpaper"/> carries the provider-neutral identity
/// (imports use the "local" provider), so a future provider that offers authorised video wallpapers can feed the same library.
/// </summary>
public sealed record VideoWallpaper
{
    public long Id { get; init; }
    public required Wallpaper Wallpaper { get; init; }

    /// <summary>The Luma-managed copy that is played. Never the user's original file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Where the video was imported from. Informational only; Luma never modifies or deletes it.</summary>
    public string? OriginalPath { get; init; }

    public string? Sha256 { get; init; }
    public long FileSizeBytes { get; init; }
    public required VideoInfo Info { get; init; }
    public string? ThumbnailPath { get; init; }

    /// <summary>A full-size still frame, used as the system wallpaper while the video isn't playing.</summary>
    public string? PosterPath { get; init; }

    public DateTimeOffset ImportedAt { get; init; }
    public bool FileExists { get; init; } = true;

    public string Key => Wallpaper.Key;
    public string Title => Wallpaper.DisplayTitle;
}

/// <summary>Facts read from the video by the playback engine itself.</summary>
public sealed record VideoInfo(int Width, int Height, TimeSpan Duration, VideoContainer Container, bool HasAudio, string? Codec = null);

/// <summary>A connected display in physical pixels (virtual-screen coordinates).</summary>
public sealed record DisplayInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary)
{
    public string Describe(int index) =>
        $"Display {index + 1}  ·  {Bounds.Width}×{Bounds.Height}{(IsPrimary ? "  ·  main" : string.Empty)}";
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PixelRect other) =>
        !other.IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);
}

/// <summary>What the desktop host should show.</summary>
/// <param name="DisplayDeviceNames">The displays to cover; never empty.</param>
public sealed record VideoPresentation(string FilePath, IReadOnlyList<string> DisplayDeviceNames, WallpaperFitMode Fit);

/// <summary>System state that affects whether the video should be decoding. Reported by the platform host.</summary>
public sealed record DesktopConditions
{
    public static DesktopConditions Normal { get; } = new();

    public bool OnBattery { get; init; }
    public bool FullscreenAppRunning { get; init; }

    /// <summary>Device names of displays whose desktop is completely hidden (fullscreen or maximised windows).</summary>
    public IReadOnlySet<string> CoveredDisplays { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool SessionLocked { get; init; }
    public bool DisplaysOff { get; init; }
    public bool Suspended { get; init; }
}

public sealed record VideoHostCapabilities(bool IsSupported, string? Notes)
{
    public static VideoHostCapabilities Unsupported(string notes) => new(false, notes);
}

/// <summary>Raised by the host when playback stops working. <see cref="Recoverable"/> failures are worth retrying.</summary>
public sealed record VideoHostFailure(string Message, bool Recoverable);

public sealed record VideoWallpaperStatus
{
    public static VideoWallpaperStatus Inactive { get; } = new();

    public VideoPlaybackState State { get; init; } = VideoPlaybackState.Inactive;
    public VideoWallpaper? Video { get; init; }
    public PauseReasons PauseReasons { get; init; }

    /// <summary>User-facing explanation for failures, recovery or a display fallback.</summary>
    public string? Message { get; init; }

    public int DisplayCount { get; init; }

    public bool IsActive => State is not (VideoPlaybackState.Inactive or VideoPlaybackState.Failed);
}

/// <param name="ResizedFrom">The original size when the imported copy was shrunk to fit the screens, otherwise null.</param>
public sealed record VideoImportResult(VideoWallpaper Video, bool AlreadyExisted, WallpaperResolution? ResizedFrom = null);
