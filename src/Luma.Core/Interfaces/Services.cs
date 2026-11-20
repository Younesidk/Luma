using Luma.Core.Enums;
using Luma.Core.Models;

namespace Luma.Core.Interfaces;

public interface ILumaPaths
{
    string DataRoot { get; }
    string DatabasePath { get; }
    string SettingsPath { get; }
    string ThumbnailCacheDirectory { get; }
    string DefaultDownloadDirectory { get; }
    string LogsDirectory { get; }

    /// <summary>Luma-managed copies of imported video wallpapers (never inside the installation folder).</summary>
    string VideosDirectory { get; }
}

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a mutation, notifies listeners and schedules a (coalesced) save.</summary>
    void Update(Action<AppSettings> mutate);

    /// <summary>Writes any pending changes immediately.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>The directory downloads are currently written to.</summary>
    string EffectiveDownloadDirectory { get; }
}

public interface IFavoritesService
{
    event EventHandler<FavoriteChangedEventArgs>? FavoriteChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);

    bool IsFavorite(string key);

    int Count { get; }

    Task SetFavoriteAsync(Wallpaper wallpaper, bool isFavorite, CancellationToken cancellationToken = default);

    /// <summary>Toggles and returns the new state.</summary>
    Task<bool> ToggleAsync(Wallpaper wallpaper, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Wallpaper>> QueryAsync(LibraryQuery query, CancellationToken cancellationToken = default);
}

public sealed class FavoriteChangedEventArgs(string key, bool isFavorite) : EventArgs
{
    public string Key { get; } = key;
    public bool IsFavorite { get; } = isFavorite;
}

public interface ICollectionService
{
    event EventHandler? CollectionsChanged;

    Task<IReadOnlyList<WallpaperCollection>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<WallpaperCollection> CreateAsync(string name, string? description, CancellationToken cancellationToken = default);

    Task RenameAsync(long collectionId, string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>Deletes the collection only; wallpapers and downloaded files are untouched.</summary>
    Task DeleteAsync(long collectionId, CancellationToken cancellationToken = default);

    Task AddWallpaperAsync(long collectionId, Wallpaper wallpaper, CancellationToken cancellationToken = default);

    Task RemoveWallpaperAsync(long collectionId, string wallpaperKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Wallpaper>> GetWallpapersAsync(long collectionId, CancellationToken cancellationToken = default);

    /// <summary>Ids of the collections that already contain the wallpaper.</summary>
    Task<IReadOnlySet<long>> GetCollectionIdsContainingAsync(string wallpaperKey, CancellationToken cancellationToken = default);
}

/// <summary>Persistence of downloaded wallpapers (the local library).</summary>
public interface IDownloadLibrary
{
    event EventHandler<LibraryChangedEventArgs>? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Local path of a downloaded wallpaper, or null if it was not downloaded (or the file vanished).</summary>
    string? GetLocalPath(string wallpaperKey);

    bool IsDownloaded(string wallpaperKey);

    Task<IReadOnlyList<DownloadedWallpaper>> QueryAsync(LibraryQuery query, CancellationToken cancellationToken = default);

    Task<DownloadedWallpaper?> FindByKeyAsync(string wallpaperKey, CancellationToken cancellationToken = default);

    Task<DownloadedWallpaper?> FindBySha256Async(string sha256, CancellationToken cancellationToken = default);

    Task<DownloadedWallpaper> AddAsync(Wallpaper wallpaper, string filePath, string? sha256, CancellationToken cancellationToken = default);

    /// <summary>Registers an existing image file without copying it.</summary>
    Task<DownloadedWallpaper> ImportFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Removes the library entry. The file is only deleted when <paramref name="deleteFile"/> is true.</summary>
    Task RemoveAsync(long downloadId, bool deleteFile, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DownloadedWallpaper>> GetRotationCandidatesAsync(RotationSource source, long? collectionId, CancellationToken cancellationToken = default);
}

public sealed class LibraryChangedEventArgs(string wallpaperKey, bool isDownloaded) : EventArgs
{
    public string WallpaperKey { get; } = wallpaperKey;
    public bool IsDownloaded { get; } = isDownloaded;
}

public interface IHistoryService
{
    event EventHandler? Changed;

    /// <summary>Records an applied wallpaper. Re-applying the same wallpaper twice in a row only refreshes the timestamp.</summary>
    Task RecordAsync(Wallpaper wallpaper, string? filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryEntry>> GetAsync(int take, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Small string-keyed store for internal state (e.g. rotation bookkeeping).</summary>
public interface IStateStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

public interface IDownloadService
{
    event EventHandler<DownloadJob>? JobAdded;

    IReadOnlyList<DownloadJob> Jobs { get; }

    /// <summary>Queues a download; returns the running job if the wallpaper is already being downloaded.</summary>
    DownloadJob Enqueue(Wallpaper wallpaper);

    /// <summary>Removes finished (completed, failed, cancelled) jobs from <see cref="Jobs"/>.</summary>
    void ClearFinished();
}

/// <summary>Observable state of one download. Raised from background threads.</summary>
public sealed class DownloadJob
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<DownloadedWallpaper?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DownloadJob(Wallpaper wallpaper)
    {
        Wallpaper = wallpaper;
    }

    public event EventHandler? Changed;

    public Wallpaper Wallpaper { get; }

    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;

    public long BytesReceived { get; private set; }

    public long? TotalBytes { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Set when the wallpaper already existed in the library (no new file was written).</summary>
    public string? Note { get; private set; }

    public CancellationToken Token => _cts.Token;

    /// <summary>Completes with the library entry, or null when failed/cancelled.</summary>
    public Task<DownloadedWallpaper?> Completion => _completion.Task;

    public double Progress => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : 0;

    public bool IsFinished => Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;

    public void Cancel()
    {
        if (IsFinished) return;
        _cts.Cancel();
    }

    public void MarkStarted() => Update(DownloadStatus.Downloading);

    public void ReportProgress(long received, long? total)
    {
        BytesReceived = received;
        TotalBytes = total;
        Raise();
    }

    public void MarkCompleted(DownloadedWallpaper result, string? note = null)
    {
        Note = note;
        Update(DownloadStatus.Completed);
        _completion.TrySetResult(result);
    }

    public void MarkFailed(string message)
    {
        ErrorMessage = message;
        Update(DownloadStatus.Failed);
        _completion.TrySetResult(null);
    }

    public void MarkCancelled()
    {
        Update(DownloadStatus.Cancelled);
        _completion.TrySetResult(null);
    }

    private void Update(DownloadStatus status)
    {
        Status = status;
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

public interface IWallpaperApplier
{
    WallpaperApplierCapabilities Capabilities { get; }

    /// <summary>Applies an image that already exists on disk. Must never throw for expected failures.</summary>
    Task<ApplyResult> ApplyAsync(string filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default);
}

/// <summary>High level "make this wallpaper my desktop background" workflow (download if needed, apply, record history).</summary>
public interface IWallpaperApplyService
{
    WallpaperApplierCapabilities Capabilities { get; }

    Task<ApplyResult> ApplyAsync(Wallpaper wallpaper, WallpaperFitMode fitMode, IProgress<double>? downloadProgress = null, CancellationToken cancellationToken = default);

    Task<ApplyResult> ApplyFileAsync(string filePath, Wallpaper? wallpaper, WallpaperFitMode fitMode, CancellationToken cancellationToken = default);
}

public interface IRotationService : IAsyncDisposable
{
    event EventHandler? Rotated;

    bool IsRunning { get; }

    DateTimeOffset? NextRotationAt { get; }

    /// <summary>Starts watching the settings and scheduling rotations. Idempotent.</summary>
    void Start();

    Task StopAsync();

    /// <summary>Rotates immediately regardless of schedule.</summary>
    Task<ApplyResult> RotateNowAsync(CancellationToken cancellationToken = default);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool CommandNotFound = false)
{
    public bool Succeeded => ExitCode == 0 && !CommandNotFound;
}

public interface IPlatformInfo
{
    bool IsWindows { get; }
    bool IsLinux { get; }
    bool IsMacOS { get; }

    string Name { get; }

    /// <summary>Linux desktop environment identifier (XDG_CURRENT_DESKTOP), lower-case; empty elsewhere.</summary>
    string DesktopEnvironment { get; }

    /// <summary>"wayland", "x11" or empty.</summary>
    string SessionType { get; }

    string? GetEnvironmentVariable(string name);
}

/// <summary>Opens folders, files and URLs in the operating system's default handler.</summary>
public interface IShellLauncher
{
    bool ShowInFolder(string filePath);

    bool OpenFolder(string directory);

    bool OpenUrl(string url);
}

/// <summary>Fetches image bytes for display. Thumbnails are disk cached, previews are not.</summary>
public interface IImageFetcher
{
    /// <summary>Gets a thumbnail through the bounded, disk cached pipeline.</summary>
    Task<byte[]> GetThumbnailAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Gets a single larger image (preview) without caching it on disk. Fails when bigger than <paramref name="maxBytes"/>.</summary>
    Task<byte[]> GetImageAsync(string url, long maxBytes, CancellationToken cancellationToken = default);

    long GetCacheSizeBytes();

    Task ClearCacheAsync(CancellationToken cancellationToken = default);
}
