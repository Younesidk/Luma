using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.Services;

/// <summary>
/// The user-facing operations on a wallpaper (favourite, download, apply, organise...). Centralising them keeps view
/// models small and guarantees that every entry point reports errors and success the same way.
/// </summary>
public sealed class WallpaperActions
{
    private readonly IFavoritesService _favorites;
    private readonly IDownloadService _downloads;
    private readonly IDownloadLibrary _library;
    private readonly IWallpaperApplyService _applyService;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly IShellLauncher _shell;
    private readonly INavigationService _navigation;
    private readonly ILogger<WallpaperActions> _logger;

    public WallpaperActions(IFavoritesService favorites, IDownloadService downloads, IDownloadLibrary library,
        IWallpaperApplyService applyService, ISettingsService settings, IDialogService dialogs, IToastService toasts,
        IShellLauncher shell, INavigationService navigation, ILogger<WallpaperActions> logger)
    {
        _favorites = favorites;
        _downloads = downloads;
        _library = library;
        _applyService = applyService;
        _settings = settings;
        _dialogs = dialogs;
        _toasts = toasts;
        _shell = shell;
        _navigation = navigation;
        _logger = logger;
    }

    public WallpaperApplierCapabilities ApplierCapabilities => _applyService.Capabilities;

    public async Task ToggleFavoriteAsync(Wallpaper wallpaper)
    {
        try
        {
            await _favorites.ToggleAsync(wallpaper);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling favourite failed for {Key}", wallpaper.Key);
            _toasts.Show("Your favourites couldn't be updated. Please try again.", ToastKind.Error);
        }
    }

    /// <summary>Queues a download. Progress reaches the UI through <see cref="DownloadProgressMessage"/>.</summary>
    public async Task DownloadAsync(Wallpaper wallpaper)
    {
        if (_library.GetLocalPath(wallpaper.Key) is { } existing)
        {
            _toasts.Show("Already in your library", ToastKind.Info, "Show in folder", () => _shell.ShowInFolder(existing));
            return;
        }

        var job = _downloads.Enqueue(wallpaper);
        var result = await job.Completion;
        if (result is not null)
        {
            var note = job.Note is null ? "Saved to your library" : job.Note;
            _toasts.Show(note, ToastKind.Success, "Show in folder", () => _shell.ShowInFolder(result.FilePath));
        }
        else if (job.Status == DownloadStatus.Failed)
        {
            _toasts.Show(job.ErrorMessage ?? "The download failed.", ToastKind.Error, "Retry", () => _ = DownloadAsync(wallpaper));
        }
    }

    public async Task<ApplyResult> ApplyAsync(Wallpaper wallpaper, WallpaperFitMode? fitMode = null, IProgress<double>? progress = null)
    {
        var fit = fitMode ?? _settings.Current.DefaultFitMode;
        try
        {
            var result = await _applyService.ApplyAsync(wallpaper, fit, progress);
            if (result.Success)
                _toasts.Show("Wallpaper applied", ToastKind.Success);
            else
                _toasts.Show(result.Message ?? "The wallpaper couldn't be applied.", ToastKind.Error);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying wallpaper {Key} failed unexpectedly", wallpaper.Key);
            _toasts.Show("Something went wrong while applying the wallpaper.", ToastKind.Error);
            return ApplyResult.Fail(ApplyFailureReason.Unknown, "Unexpected failure");
        }
    }

    public async Task<ApplyResult> ApplyLocalAsync(DownloadedWallpaper download)
    {
        try
        {
            var result = await _applyService.ApplyFileAsync(download.FilePath, download.Wallpaper, _settings.Current.DefaultFitMode);
            if (result.Success) _toasts.Show("Wallpaper applied", ToastKind.Success);
            else _toasts.Show(result.Message ?? "The wallpaper couldn't be applied.", ToastKind.Error);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying local wallpaper failed");
            _toasts.Show("Something went wrong while applying the wallpaper.", ToastKind.Error);
            return ApplyResult.Fail(ApplyFailureReason.Unknown, "Unexpected failure");
        }
    }

    public Task AddToCollectionAsync(Wallpaper wallpaper) => _dialogs.PickCollectionAsync(wallpaper);

    public void OpenSource(Wallpaper wallpaper)
    {
        var url = wallpaper.SourcePageUrl ?? wallpaper.FullUrl;
        if (url is null || !_shell.OpenUrl(url))
            _toasts.Show("That page couldn't be opened.", ToastKind.Warning);
    }

    public void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !_shell.OpenUrl(url))
            _toasts.Show("That link couldn't be opened.", ToastKind.Warning);
    }

    public void ShowInFolder(string path)
    {
        if (!_shell.ShowInFolder(path))
            _toasts.Show("The file couldn't be found. It may have been moved or deleted.", ToastKind.Warning);
    }

    public async Task RemoveFromLibraryAsync(DownloadedWallpaper download)
    {
        try
        {
            await _library.RemoveAsync(download.Id, deleteFile: false);
            _toasts.Show("Removed from your library. The file was kept on disk.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing library entry failed");
            _toasts.Show("The wallpaper couldn't be removed from your library.", ToastKind.Error);
        }
    }

    /// <summary>Permanently deletes the downloaded file after an explicit confirmation (unless the user disabled it).</summary>
    public async Task<bool> DeleteFileAsync(DownloadedWallpaper download)
    {
        if (_settings.Current.ConfirmBeforeDelete)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete this file?",
                $"\"{Path.GetFileName(download.FilePath)}\" will be permanently deleted from your disk and removed from your library. This can't be undone.",
                "Delete file", destructive: true);
            if (!confirmed) return false;
        }

        try
        {
            await _library.RemoveAsync(download.Id, deleteFile: true);
            _toasts.Show("File deleted", ToastKind.Info);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting file failed");
            _toasts.Show(ex is Core.Exceptions.LumaException ? ex.Message : "The file couldn't be deleted.", ToastKind.Error);
            return false;
        }
    }

    public void GoToDownloads() => _navigation.NavigateTo(AppPage.Downloads);

    public static string DescribeSize(long? bytes) => ByteSizeFormatter.Format(bytes);
}
