using Luma.Core.Models;

namespace Luma.Core.Interfaces;

/// <summary>Imported video wallpapers: the files in Luma's managed folder and their metadata.</summary>
public interface IVideoLibrary
{
    event EventHandler? Changed;

    /// <summary>The folder imported videos are copied into.</summary>
    string Directory { get; }

    Task<IReadOnlyList<VideoWallpaper>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<VideoWallpaper?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates <paramref name="sourcePath"/>, copies it into the managed folder and verifies that the player can decode it.
    /// The source file is only ever read. Importing identical content again returns the existing entry.
    /// Throws <see cref="Exceptions.VideoNotPlayableException"/> with a user-facing reason.
    /// </summary>
    Task<VideoImportResult> ImportAsync(string sourcePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes the entry and the managed copy, thumbnail and still frame. Files outside the managed folder are never deleted.</summary>
    Task RemoveAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Deletes leftovers of interrupted imports and files no entry refers to. Safe to call at start-up.</summary>
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Re-encodes videos that are much larger than the screens. A wallpaper is never shown bigger than the display, but the
/// decoder keeps full-size frames in memory: a 4K video on a 1080p screen costs about four times the memory of a 1080p one.
/// </summary>
public interface IVideoOptimizer
{
    bool IsSupported { get; }

    /// <summary>
    /// Writes the video stream of <paramref name="inputPath"/> to <paramref name="outputPath"/> (H.264 in MP4, no audio) scaled
    /// to <paramref name="width"/>×<paramref name="height"/>. The input is only read. Throws <see cref="Exceptions.VideoNotPlayableException"/>.
    /// </summary>
    Task ShrinkAsync(string inputPath, string outputPath, int width, int height, TimeSpan duration, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Opens a video with the real playback engine to read its facts and grab still frames.</summary>
public interface IVideoProbe
{
    VideoHostCapabilities Capabilities { get; }

    /// <summary>Throws <see cref="Exceptions.VideoNotPlayableException"/> when the engine can't play the file.</summary>
    Task<VideoProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <param name="ThumbnailPng">Small frame for the gallery.</param>
/// <param name="PosterPng">Large frame used as the system wallpaper while the video isn't playing.</param>
public sealed record VideoProbeResult(int Width, int Height, TimeSpan Duration, bool HasAudio, byte[] ThumbnailPng, byte[] PosterPng, string? Codec = null);

/// <summary>
/// Platform component that plays a video behind the desktop icons. It owns native windows and decoders and reports
/// system conditions (battery, fullscreen apps, lock, sleep) so the service can decide when to pause.
/// </summary>
public interface IDesktopVideoHost : IAsyncDisposable
{
    VideoHostCapabilities Capabilities { get; }

    DesktopConditions Conditions { get; }

    event EventHandler<DesktopConditions>? ConditionsChanged;

    /// <summary>Playback stopped and the host could not repair it on its own.</summary>
    event EventHandler<VideoHostFailure>? Failed;

    /// <summary>Displays were connected, disconnected or rearranged.</summary>
    event EventHandler? DisplaysChanged;

    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>
    /// Shows the video (replacing whatever is shown). When the same file is already loaded only the outputs are updated,
    /// so it is never decoded twice. Completes once the first frame is on screen; throws <see cref="Exceptions.VideoHostException"/>.
    /// </summary>
    Task ShowAsync(VideoPresentation presentation, bool startPaused, CancellationToken cancellationToken = default);

    void SetPaused(bool paused);

    /// <summary>Stops playback, releases the decoder and removes every window it put on the desktop.</summary>
    Task HideAsync();
}

/// <summary>Plays a video inside a window of Luma's own interface (the preview before applying).</summary>
public interface IVideoPreviewFactory
{
    bool IsSupported { get; }

    /// <summary>Creates a child window of <paramref name="parentWindow"/> that plays <paramref name="filePath"/> muted and looping.</summary>
    IVideoPreviewSurface Create(nint parentWindow, string filePath);
}

public interface IVideoPreviewSurface : IDisposable
{
    nint Handle { get; }

    /// <summary>Raised (on any thread) when the video can't be played.</summary>
    event EventHandler<string>? Failed;

    void SetPaused(bool paused);
}

/// <summary>Orchestrates the video wallpaper: selection, persistence, pausing policy and recovery.</summary>
public interface IVideoWallpaperService : IAsyncDisposable
{
    VideoHostCapabilities Capabilities { get; }

    VideoWallpaperStatus Status { get; }

    /// <summary>Raised on any thread.</summary>
    event EventHandler<VideoWallpaperStatus>? StatusChanged;

    bool IsActive { get; }

    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>Re-applies the video that was active when Luma last closed. Problems are reported through <see cref="Status"/>.</summary>
    Task RestoreAsync(CancellationToken cancellationToken = default);

    Task<ApplyResult> ApplyAsync(long videoId, CancellationToken cancellationToken = default);

    /// <summary>Turns the video wallpaper off (its still frame stays on the desktop unless a static wallpaper replaced it).</summary>
    Task DeactivateAsync();

    Task SetUserPausedAsync(bool paused);

    /// <summary>Removes a video from the library, stopping it first when it is the active wallpaper.</summary>
    Task RemoveVideoAsync(long videoId, CancellationToken cancellationToken = default);
}
