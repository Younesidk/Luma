using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.ViewModels;

/// <summary>One imported video in the Live gallery.</summary>
public sealed partial class VideoItemViewModel(VideoWallpaper video, LiveWallpapersViewModel owner) : ObservableObject
{
    public VideoWallpaper Video { get; } = video;
    public long Id => Video.Id;
    public string Title => Video.Title;
    public string FilePath => Video.FilePath;
    public string? ThumbnailSource => Video.ThumbnailPath;
    public bool FileMissing => !Video.FileExists;
    public bool FileExists => Video.FileExists;
    public string DurationText => FormatDuration(Video.Info.Duration);
    public string ResolutionText => $"{Video.Info.Width}×{Video.Info.Height}";
    public string FormatText => Video.Info.Container switch
    {
        VideoContainer.WebM => "WebM",
        VideoContainer.QuickTime => "MOV",
        _ => "MP4"
    };
    public string MetaText => $"{ResolutionText}  ·  {FormatText}  ·  {ByteSizeFormatter.Format(Video.FileSizeBytes)}";

    [ObservableProperty]
    private bool _isActive;

    [RelayCommand]
    private void Preview() => owner.OpenPreview(this);

    [RelayCommand]
    private Task ApplyAsync() => owner.ApplyAsync(this);

    [RelayCommand]
    private void ShowInFolder() => owner.ShowInFolder(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);

    public static string FormatDuration(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? "—" : duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
}

/// <summary>
/// The Live page: imported video wallpapers, the in-app preview, and the controls of the video that is currently on the
/// desktop. All playback decisions live in <see cref="IVideoWallpaperService"/>; this class only presents them.
/// </summary>
public sealed partial class LiveWallpapersViewModel : PageViewModel
{
    private readonly IVideoLibrary _library;
    private readonly IVideoWallpaperService _video;
    private readonly ISettingsService _settings;
    private readonly IFileDialogService _files;
    private readonly IToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private readonly ShellStartup _startup;
    private readonly ILogger<LiveWallpapersViewModel> _logger;
    private bool _active;

    public LiveWallpapersViewModel(IVideoLibrary library, IVideoWallpaperService video, ISettingsService settings, IFileDialogService files,
        IToastService toasts, IDialogService dialogs, IShellLauncher shell, ShellStartup startup, ILogger<LiveWallpapersViewModel> logger)
    {
        _library = library;
        _video = video;
        _settings = settings;
        _files = files;
        _toasts = toasts;
        _dialogs = dialogs;
        _shell = shell;
        _startup = startup;
        _logger = logger;

        FitOptions = VideoLayout.SupportedFitModes.Select(m => new Option<WallpaperFitMode>(m switch
        {
            WallpaperFitMode.Fit => "Fit (show all)",
            WallpaperFitMode.Stretch => "Stretch",
            _ => "Fill (crop to screen)"
        }, m)).ToList();

        _status = video.Status;
        video.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => ApplyStatus(status));
        library.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_active) _ = ReloadAsync();
        });
    }

    public override string Title => "Live wallpapers";

    public bool IsSupported => _video.Capabilities.IsSupported;
    public string? UnsupportedReason => _video.Capabilities.Notes;

    public ObservableCollection<VideoItemViewModel> Items { get; } = [];
    public ObservableCollection<Option<string>> DisplayOptions { get; } = [];
    public IReadOnlyList<Option<WallpaperFitMode>> FitOptions { get; }

    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => !IsLoading && Items.Count == 0 && IsSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImporting))]
    private string? _importStatus;

    public bool IsImporting => ImportStatus is not null;

    [ObservableProperty]
    private double _importProgress;

    // ----- now playing -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveVideo), nameof(ActiveTitle), nameof(ActiveThumbnail), nameof(StatusText), nameof(IsPaused),
        nameof(CanPause), nameof(CanResume), nameof(HasStatusMessage), nameof(StatusMessage), nameof(IsFailed), nameof(HeaderText))]
    private VideoWallpaperStatus _status;

    public bool HasActiveVideo => Status.Video is not null && Status.State != VideoPlaybackState.Inactive;
    public string ActiveTitle => Status.Video?.Title ?? string.Empty;
    public string? ActiveThumbnail => Status.Video?.ThumbnailPath;
    public bool IsPaused => Status.State == VideoPlaybackState.Paused;
    public bool IsFailed => Status.State == VideoPlaybackState.Failed;
    public bool CanPause => Status.State == VideoPlaybackState.Playing || (Status.State == VideoPlaybackState.Paused && !Status.PauseReasons.HasFlag(PauseReasons.User));
    public bool CanResume => Status.PauseReasons.HasFlag(PauseReasons.User);
    public string HeaderText => IsFailed ? "LIVE WALLPAPER STOPPED" : "NOW ON YOUR DESKTOP";
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(Status.Message);
    public string? StatusMessage => Status.Message;

    public string StatusText => Status.State switch
    {
        VideoPlaybackState.Starting => "Starting…",
        VideoPlaybackState.Recovering => "Restarting the video…",
        VideoPlaybackState.Failed => "Stopped",
        VideoPlaybackState.Playing => $"Playing on {DescribeDisplays(Status.DisplayCount)}",
        VideoPlaybackState.Paused => VideoPlaybackPolicy.Describe(Status.PauseReasons),
        _ => string.Empty
    };

    // ----- preview -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewOpen))]
    private VideoItemViewModel? _previewItem;

    [ObservableProperty]
    private string? _previewError;

    [ObservableProperty]
    private bool _isApplying;

    public bool IsPreviewOpen => PreviewItem is not null;

    public Option<string>? SelectedDisplay
    {
        get => DisplayOptions.FirstOrDefault(o => string.Equals(o.Value, _settings.Current.VideoDisplayTarget, StringComparison.OrdinalIgnoreCase))
               ?? DisplayOptions.FirstOrDefault();
        set
        {
            if (value is null || string.Equals(value.Value, _settings.Current.VideoDisplayTarget, StringComparison.OrdinalIgnoreCase)) return;
            _settings.Update(s => s.VideoDisplayTarget = value.Value);
            OnPropertyChanged();
        }
    }

    public Option<WallpaperFitMode> SelectedFit
    {
        get => FitOptions.FirstOrDefault(o => o.Value == _settings.Current.VideoFitMode) ?? FitOptions[0];
        set
        {
            if (value is null || value.Value == _settings.Current.VideoFitMode) return;
            _settings.Update(s => s.VideoFitMode = value.Value);
            OnPropertyChanged();
        }
    }

    public bool ShowDisplayChoice => DisplayOptions.Count > 2;

    public override async Task OnActivatedAsync()
    {
        _active = true;
        RebuildDisplayOptions();
        await _startup.BeginAsync();
        await ReloadAsync();
    }

    public override void OnDeactivated()
    {
        _active = false;
        ClosePreview(); // never keep a decoder running for a page nobody sees
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsLoading = Items.Count == 0;
        try
        {
            await _startup.Ready;
            var videos = await _library.GetAllAsync();
            Items.Clear();
            foreach (var video in videos) Items.Add(new VideoItemViewModel(video, this));
            MarkActive();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading live wallpapers failed");
            _toasts.Show("Your live wallpapers couldn't be loaded.", ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!IsSupported || IsImporting) return;
        var paths = await _files.PickVideosAsync();
        if (paths.Count == 0) return;

        VideoWallpaper? last = null;
        var imported = 0;
        string? resizedNote = null;
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var counter = paths.Count == 1 ? string.Empty : $" {i + 1} of {paths.Count}:";
                ImportStatus = $"Importing{counter} {Path.GetFileName(path)}…";
                ImportProgress = 0;
                try
                {
                    var progress = new Progress<double>(p =>
                    {
                        ImportProgress = p * 100;
                        // The library reports above 90 % only while it re-encodes an oversized video.
                        if (p > 0.9 && p < 1) ImportStatus = $"Resizing{counter} {Path.GetFileName(path)} for your screen…";
                    });
                    var result = await _library.ImportAsync(path, progress);
                    last = result.Video;
                    if (result.AlreadyExisted) _toasts.Show($"\"{result.Video.Title}\" is already in your live wallpapers.", ToastKind.Info);
                    else imported++;
                    if (result.ResizedFrom is { } from)
                        resizedNote = $"resized from {from.Width}×{from.Height} to {result.Video.Info.Width}×{result.Video.Info.Height} to fit your screen";
                }
                catch (VideoNotPlayableException ex)
                {
                    failures.Add(ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Importing {Path} failed", path);
                    failures.Add($"\"{Path.GetFileName(path)}\" couldn't be imported.");
                }
            }
        }
        finally
        {
            ImportStatus = null;
        }

        await ReloadAsync();
        if (imported > 0)
        {
            var text = imported == 1 ? "Video imported" : $"Imported {imported} videos";
            if (resizedNote is not null) text += imported == 1 ? $" and {resizedNote}" : " (oversized ones were resized to fit your screen)";
            _toasts.Show(text, ToastKind.Success);
        }
        if (failures.Count > 0) _toasts.Show(failures[0] + (failures.Count > 1 ? $" (+{failures.Count - 1} more)" : string.Empty), ToastKind.Error);

        // One new video: show it straight away so the user can try it before applying.
        if (paths.Count == 1 && last is not null && Items.FirstOrDefault(v => v.Id == last.Id) is { } item) OpenPreview(item);
    }

    internal void OpenPreview(VideoItemViewModel item)
    {
        if (item.FileMissing)
        {
            _toasts.Show("The video file is missing. Remove it and import it again.", ToastKind.Warning);
            return;
        }
        RebuildDisplayOptions();
        PreviewError = null;
        PreviewItem = item;
    }

    [RelayCommand]
    private void ClosePreview()
    {
        PreviewItem = null;
        PreviewError = null;
    }

    [RelayCommand]
    private Task ApplyPreviewAsync() => PreviewItem is { } item ? ApplyAsync(item) : Task.CompletedTask;

    internal async Task ApplyAsync(VideoItemViewModel item)
    {
        if (IsApplying) return;
        IsApplying = true;
        try
        {
            var result = await _video.ApplyAsync(item.Id);
            if (result.Success)
            {
                ClosePreview();
                _toasts.Show("Live wallpaper applied", ToastKind.Success);
            }
            else
            {
                _toasts.Show(result.Message ?? "The video couldn't be set as your wallpaper.", ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying video {Id} failed", item.Id);
            _toasts.Show("Something went wrong while applying the video.", ToastKind.Error);
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private Task RetryAsync() =>
        Status.Video is { } video ? ApplyAsync(Items.FirstOrDefault(i => i.Id == video.Id) ?? new VideoItemViewModel(video, this)) : Task.CompletedTask;

    [RelayCommand]
    private Task PauseAsync() => _video.SetUserPausedAsync(true);

    [RelayCommand]
    private Task ResumeAsync() => _video.SetUserPausedAsync(false);

    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            await _video.DeactivateAsync();
            _toasts.Show("Live wallpaper turned off. Its still frame stays on your desktop.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping the live wallpaper failed");
            _toasts.Show("The live wallpaper couldn't be turned off.", ToastKind.Error);
        }
    }

    internal void ShowInFolder(VideoItemViewModel item)
    {
        if (!_shell.ShowInFolder(item.FilePath)) _toasts.Show("The file couldn't be found. It may have been deleted.", ToastKind.Warning);
    }

    internal async Task RemoveAsync(VideoItemViewModel item)
    {
        var active = item.IsActive;
        var confirmed = await _dialogs.ConfirmAsync("Remove this live wallpaper?",
            $"\"{item.Title}\" will be removed from Luma and its copy in Luma's video folder deleted. The file you imported it from isn't touched."
            + (active ? " It is your current wallpaper, so the video will stop." : string.Empty),
            "Remove", destructive: true);
        if (!confirmed) return;

        try
        {
            if (PreviewItem?.Id == item.Id) ClosePreview();
            await _video.RemoveVideoAsync(item.Id);
            _toasts.Show("Live wallpaper removed", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing video {Id} failed", item.Id);
            _toasts.Show("The video couldn't be removed.", ToastKind.Error);
        }
        await ReloadAsync();
    }

    [RelayCommand]
    private void OpenVideoFolder()
    {
        Directory.CreateDirectory(_library.Directory);
        if (!_shell.OpenFolder(_library.Directory)) _toasts.Show("The folder couldn't be opened.", ToastKind.Warning);
    }

    private void ApplyStatus(VideoWallpaperStatus status)
    {
        var previous = Status;
        Status = status;
        MarkActive();
        // Failures that happen in the background (restore at start-up, giving up after retries) need a visible explanation.
        // A failed apply is already reported by ApplyAsync.
        if (!IsApplying && status.State == VideoPlaybackState.Failed && previous.State != VideoPlaybackState.Failed && status.Message is { } message)
            _toasts.Show(message, ToastKind.Error);
    }

    private void MarkActive()
    {
        var activeId = Status.State != VideoPlaybackState.Inactive && Status.State != VideoPlaybackState.Failed ? Status.Video?.Id : null;
        foreach (var item in Items) item.IsActive = item.Id == activeId;
    }

    private void RebuildDisplayOptions()
    {
        DisplayOptions.Clear();
        var displays = _video.GetDisplays();
        DisplayOptions.Add(new Option<string>(displays.Count > 1 ? "All displays" : "This display", VideoLayout.AllDisplays));
        for (var i = 0; i < displays.Count; i++) DisplayOptions.Add(new Option<string>(displays[i].Describe(i), displays[i].DeviceName));
        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(ShowDisplayChoice));
    }

    private static string DescribeDisplays(int count) => count switch
    {
        <= 1 => "your desktop",
        _ => $"{count} displays"
    };
}
