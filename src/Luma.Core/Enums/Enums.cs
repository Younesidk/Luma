namespace Luma.Core.Enums;

/// <summary>How a wallpaper is laid out on the desktop.</summary>
public enum WallpaperFitMode
{
    Fill,
    Fit,
    Stretch,
    Tile,
    Center,
    Span
}

public enum WallpaperSort
{
    Relevance,
    Newest,
    MostViewed,
    MostFavorited,
    TopRated,
    Random
}

public enum TopRange
{
    Day,
    ThreeDays,
    Week,
    Month,
    ThreeMonths,
    SixMonths,
    Year
}

[Flags]
public enum WallpaperCategories
{
    None = 0,
    General = 1,
    Anime = 2,
    People = 4,
    All = General | Anime | People
}

[Flags]
public enum ContentPurity
{
    None = 0,
    Safe = 1,
    Sketchy = 2,
    Explicit = 4
}

/// <summary>Provider-neutral aspect ratio filter.</summary>
public enum AspectRatioFilter
{
    Any,
    Wide16x9,
    Wide16x10,
    UltraWide21x9,
    Portrait,
    Square
}

/// <summary>Provider-neutral minimum resolution presets.</summary>
public enum MinimumResolution
{
    Any,
    FullHd,
    QuadHd,
    UltraHd4K
}

/// <summary>Content type filter (used by providers that distinguish photographs from artwork).</summary>
public enum ContentKind
{
    Any,
    Photograph,
    Illustration,
    DigitizedArtwork
}

public enum LicenseFilter
{
    Any,
    Commercial,
    Modification
}

public enum ThemePreference
{
    System,
    Dark,
    Light
}

public enum GalleryDensity
{
    Comfortable,
    Compact
}

public enum ProviderErrorKind
{
    Network,
    Timeout,
    RateLimited,
    Unauthorized,
    ServerError,
    InvalidResponse,
    NotFound,
    Disabled,
    Unknown
}

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

public enum RotationSource
{
    AllDownloads,
    Favorites,
    Collection
}

public enum LibrarySort
{
    Newest,
    Oldest,
    Name,
    Resolution,
    FileSize
}

/// <summary>How the interface is drawn. Efficient uses the CPU and far less memory; Accelerated uses the GPU.</summary>
public enum RenderingMode
{
    Efficient,
    Accelerated
}

public enum ApplyFailureReason
{
    None,
    UnsupportedPlatform,
    UnsupportedFitMode,
    FileNotFound,
    InvalidPath,
    CommandNotFound,
    OperatingSystemRejected,
    Unknown
}

/// <summary>Video containers Luma accepts. The codec inside is checked by actually opening the file with the player.</summary>
public enum VideoContainer
{
    Mp4,
    QuickTime,
    WebM
}

public enum VideoPlaybackState
{
    /// <summary>No video wallpaper is selected (a static wallpaper is in use).</summary>
    Inactive,
    Starting,
    Playing,
    Paused,

    /// <summary>Playback stopped unexpectedly and Luma is retrying.</summary>
    Recovering,

    /// <summary>Playback could not be (re)started; the still frame stays on the desktop.</summary>
    Failed
}

/// <summary>Why the video wallpaper is currently paused. Several can apply at once.</summary>
[Flags]
public enum PauseReasons
{
    None = 0,
    User = 1,
    FullscreenApp = 2,
    OnBattery = 4,
    DesktopHidden = 8,
    SessionLocked = 16,
    DisplaysOff = 32,
    Suspended = 64
}
