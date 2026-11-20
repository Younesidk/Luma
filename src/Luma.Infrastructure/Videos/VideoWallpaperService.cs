using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Videos;

/// <summary>
/// Platform-neutral control of the video wallpaper. It remembers which video is active (state store, so "Reset settings"
/// never drops it), restores it on start-up, applies the pause policy to the conditions the host reports, retries failed
/// playback a bounded number of times, and puts the video's still frame on the desktop as the system wallpaper so the
/// desktop shows a frame of it whenever the video itself can't play (Luma closed, failure, unsupported session).
/// </summary>
public sealed class VideoWallpaperService : IVideoWallpaperService
{
    public const string ActiveKey = "video.active";
    public const string PausedKey = "video.paused";

    private readonly IVideoLibrary _library;
    private readonly IDesktopVideoHost _host;
    private readonly IWallpaperApplier _applier;
    private readonly ISettingsService _settings;
    private readonly IStateStore _state;
    private readonly ILogger<VideoWallpaperService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VideoRecoveryPolicy _recovery = new();
    private readonly object _statusLock = new();

    private VideoWallpaper? _active;
    private IReadOnlyList<string> _shownDisplays = [];
    private bool _userPaused;
    private string? _displayNote;
    private CancellationTokenSource? _retryCts;
    private (string Target, WallpaperFitMode Fit, bool Fullscreen, bool Battery) _lastSettings;
    private VideoWallpaperStatus _status = VideoWallpaperStatus.Inactive;
    private bool _disposed;

    public VideoWallpaperService(IVideoLibrary library, IDesktopVideoHost host, IWallpaperApplier applier, ISettingsService settings,
        IStateStore state, ILogger<VideoWallpaperService> logger, TimeProvider? time = null)
    {
        _library = library;
        _host = host;
        _applier = applier;
        _settings = settings;
        _state = state;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastSettings = Snapshot(settings.Current);

        _host.ConditionsChanged += OnConditionsChanged;
        _host.Failed += OnHostFailed;
        _host.DisplaysChanged += OnDisplaysChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public VideoHostCapabilities Capabilities => _host.Capabilities;

    public VideoWallpaperStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    public event EventHandler<VideoWallpaperStatus>? StatusChanged;

    public bool IsActive => Status.IsActive;

    public IReadOnlyList<DisplayInfo> GetDisplays() => _host.Capabilities.IsSupported ? _host.GetDisplays() : [];

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.Capabilities.IsSupported) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!long.TryParse(await _state.GetAsync(ActiveKey, cancellationToken), out var id)) return;
            _userPaused = await _state.GetAsync(PausedKey, cancellationToken) == "true";

            var video = await _library.GetAsync(id, cancellationToken);
            if (video is null || !video.FileExists)
            {
                await _state.SetAsync(ActiveKey, null, cancellationToken);
                var title = video?.Title ?? "Your live wallpaper";
                SetStatus(VideoWallpaperStatus.Inactive with
                {
                    State = VideoPlaybackState.Failed,
                    Video = video,
                    Message = $"\"{title}\" couldn't be restored because its video file is missing."
                });
                _logger.LogWarning("Active video {Id} could not be restored: missing {What}", id, video is null ? "entry" : "file");
                return;
            }

            _active = video;
            SetStatus(new VideoWallpaperStatus { State = VideoPlaybackState.Starting, Video = video });
            try
            {
                await ShowCoreAsync(video, cancellationToken);
            }
            catch (VideoHostException ex)
            {
                _logger.LogWarning(ex, "Restoring the video wallpaper failed");
                await ScheduleRecoveryOrFailAsync(new VideoHostFailure(ex.Message, ex.Recoverable));
            }
            catch (VideoHostBusyException ex)
            {
                // Another Luma is playing it: leave the shared selection alone.
                _active = null;
                SetStatus(new VideoWallpaperStatus { State = VideoPlaybackState.Failed, Video = video, Message = ex.Message });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyResult> ApplyAsync(long videoId, CancellationToken cancellationToken = default)
    {
        if (!_host.Capabilities.IsSupported)
            return ApplyResult.Fail(ApplyFailureReason.UnsupportedPlatform, _host.Capabilities.Notes ?? "Video wallpapers aren't supported here.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var video = await _library.GetAsync(videoId, cancellationToken);
            if (video is null) return ApplyResult.Fail(ApplyFailureReason.FileNotFound, "That video is no longer in your library.");
            if (!video.FileExists)
                return ApplyResult.Fail(ApplyFailureReason.FileNotFound, "The video file is missing. Remove it and import it again.");

            CancelRetry();
            _recovery.Reset();
            var previousPaused = _userPaused;
            _userPaused = false; // choosing a video means wanting to see it
            var previous = _active;
            _active = video;
            SetStatus(new VideoWallpaperStatus { State = VideoPlaybackState.Starting, Video = video });

            try
            {
                await ShowCoreAsync(video, cancellationToken);
            }
            catch (VideoHostBusyException ex)
            {
                // Another Luma owns the desktop video: nothing changed here, and the shared selection must stay as it is.
                _active = previous;
                _userPaused = previousPaused;
                SetStatus(new VideoWallpaperStatus { State = VideoPlaybackState.Failed, Video = video, Message = ex.Message });
                return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, ex.Message);
            }
            catch (VideoHostException ex)
            {
                _logger.LogWarning(ex, "Showing video {Id} failed", videoId);
                _active = null;
                await _host.HideAsync();
                await _state.SetAsync(ActiveKey, null, CancellationToken.None);
                SetStatus(new VideoWallpaperStatus { State = VideoPlaybackState.Failed, Video = video, Message = ex.Message });
                if (previous is not null) _logger.LogInformation("The previous video wallpaper was stopped because the new one failed");
                return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, ex.Message);
            }

            await _state.SetAsync(ActiveKey, video.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
            await _state.SetAsync(PausedKey, null, CancellationToken.None);
            await ApplyPosterAsync(video);
            return ApplyResult.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeactivateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DeactivateCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetUserPausedAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            _userPaused = paused;
            await _state.SetAsync(PausedKey, paused ? "true" : null);
            Reevaluate();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveVideoAsync(long videoId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Stop first so the player releases the file before it is deleted.
            if (_active?.Id == videoId || Status.Video?.Id == videoId) await DeactivateCoreAsync();
            await _library.RemoveAsync(videoId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _host.ConditionsChanged -= OnConditionsChanged;
        _host.Failed -= OnHostFailed;
        _host.DisplaysChanged -= OnDisplaysChanged;
        _settings.Changed -= OnSettingsChanged;
        CancelRetry();

        // Shutting down is not "turning it off": the active video stays recorded and comes back on the next start.
        await _gate.WaitAsync();
        try
        {
            if (_active is not null) await _host.HideAsync();
            _active = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping the video wallpaper during shutdown failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ----- core (call with the gate held) -----

    private async Task ShowCoreAsync(VideoWallpaper video, CancellationToken ct)
    {
        var settings = _settings.Current;
        var displays = VideoLayout.ResolveDisplays(_host.GetDisplays(), settings.VideoDisplayTarget, out var fellBack);
        if (displays.Count == 0) throw new VideoHostException("No display was found to show the video on.");

        var names = displays.Select(d => d.DeviceName).ToList();
        var reasons = Evaluate(names);
        await _host.ShowAsync(new VideoPresentation(video.FilePath, names, VideoLayout.Normalize(settings.VideoFitMode)), reasons != PauseReasons.None, ct);

        _shownDisplays = names;
        _displayNote = fellBack ? "The chosen display isn't connected, so the video is on the main display." : null;
        PublishPlaybackStatus(reasons);
    }

    private async Task DeactivateCoreAsync()
    {
        CancelRetry();
        _recovery.Reset();
        _active = null;
        _shownDisplays = [];
        await _host.HideAsync();
        await _state.SetAsync(ActiveKey, null);
        SetStatus(VideoWallpaperStatus.Inactive);
    }

    private void Reevaluate()
    {
        if (_active is null || Status.State is not (VideoPlaybackState.Playing or VideoPlaybackState.Paused)) return;
        var reasons = Evaluate(_shownDisplays);
        _host.SetPaused(reasons != PauseReasons.None);
        PublishPlaybackStatus(reasons);
    }

    private PauseReasons Evaluate(IReadOnlyCollection<string> displays)
    {
        var s = _settings.Current;
        return VideoPlaybackPolicy.Evaluate(_host.Conditions, _userPaused, s.VideoPauseOnFullscreen, s.VideoPauseOnBattery, displays);
    }

    private void PublishPlaybackStatus(PauseReasons reasons)
    {
        if (reasons == PauseReasons.None) _recovery.OnPlaying(_time.GetUtcNow());
        SetStatus(new VideoWallpaperStatus
        {
            State = reasons == PauseReasons.None ? VideoPlaybackState.Playing : VideoPlaybackState.Paused,
            Video = _active,
            PauseReasons = reasons,
            DisplayCount = _shownDisplays.Count,
            Message = _displayNote
        });
    }

    private async Task ScheduleRecoveryOrFailAsync(VideoHostFailure failure)
    {
        var video = _active;
        if (video is null) return;

        var delay = failure.Recoverable ? _recovery.NextDelay(_time.GetUtcNow()) : null;
        if (delay is null)
        {
            _logger.LogError("Giving up on video wallpaper {Id}: {Message}", video.Id, failure.Message);
            _active = null;
            await _host.HideAsync();
            if (!failure.Recoverable) await _state.SetAsync(ActiveKey, null);
            SetStatus(new VideoWallpaperStatus
            {
                State = VideoPlaybackState.Failed,
                Video = video,
                Message = $"\"{video.Title}\" stopped playing: {failure.Message} Its still frame stays on your desktop."
            });
            return;
        }

        _logger.LogWarning("Video wallpaper failed ({Message}); retrying in {Delay} (attempt {Attempt} of {Max})",
            failure.Message, delay, _recovery.Attempts, _recovery.MaxAttempts);
        SetStatus(new VideoWallpaperStatus { State = VideoPlaybackState.Recovering, Video = video, Message = "Restarting the video…" });

        CancelRetry();
        var cts = _retryCts = new CancellationTokenSource();
        _ = RetryAsync(video, delay.Value, cts.Token);
    }

    private async Task RetryAsync(VideoWallpaper video, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct);
            await _gate.WaitAsync(ct);
            try
            {
                if (!ReferenceEquals(_active, video) || ct.IsCancellationRequested) return;
                try
                {
                    await ShowCoreAsync(video, ct);
                    _logger.LogInformation("Video wallpaper recovered");
                }
                catch (VideoHostException ex)
                {
                    await ScheduleRecoveryOrFailAsync(new VideoHostFailure(ex.Message, ex.Recoverable));
                }
                catch (VideoHostBusyException ex)
                {
                    await YieldToOtherInstanceAsync(video, ex);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a new apply / deactivate / shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video wallpaper recovery failed unexpectedly");
        }
    }

    /// <summary>Another Luma process took over the desktop video: stop here without touching the shared selection.</summary>
    private async Task YieldToOtherInstanceAsync(VideoWallpaper video, VideoHostBusyException ex)
    {
        CancelRetry();
        _active = null;
        await _host.HideAsync();
        SetStatus(new VideoWallpaperStatus { State = VideoPlaybackState.Failed, Video = video, Message = ex.Message });
    }

    private async Task ApplyPosterAsync(VideoWallpaper video)
    {
        if (video.PosterPath is null || !File.Exists(video.PosterPath)) return;
        try
        {
            var fit = VideoLayout.Normalize(_settings.Current.VideoFitMode);
            var result = await _applier.ApplyAsync(video.PosterPath, fit);
            if (!result.Success) _logger.LogWarning("The video's still frame couldn't be set as the system wallpaper: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            // Purely cosmetic fallback; the video itself is playing.
            _logger.LogWarning(ex, "Setting the video's still frame as the system wallpaper failed");
        }
    }

    private void CancelRetry()
    {
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _retryCts = null;
    }

    private void SetStatus(VideoWallpaperStatus status)
    {
        VideoWallpaperStatus previous;
        lock (_statusLock)
        {
            previous = _status;
            _status = status;
        }
        if (previous.State != status.State || previous.PauseReasons != status.PauseReasons || previous.Video?.Id != status.Video?.Id)
        {
            _logger.LogInformation("Live wallpaper: {State} (video {Id}, pause reasons: {Reasons}, displays: {Displays})",
                status.State, status.Video?.Id, status.PauseReasons, status.DisplayCount);
        }
        StatusChanged?.Invoke(this, status);
    }

    // ----- events from the host and settings -----

    private void OnConditionsChanged(object? sender, DesktopConditions conditions) => RunExclusive(() =>
    {
        Reevaluate();
        return Task.CompletedTask;
    });

    private void OnHostFailed(object? sender, VideoHostFailure failure) =>
        RunExclusive(() => _active is not null ? ScheduleRecoveryOrFailAsync(failure) : Task.CompletedTask);

    private void OnDisplaysChanged(object? sender, EventArgs e) => RunExclusive(ReshowAsync);

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var next = Snapshot(settings);
        var previous = _lastSettings;
        _lastSettings = next;
        if (next == previous) return;

        if (next.Target != previous.Target || next.Fit != previous.Fit) RunExclusive(ReshowAsync);
        else RunExclusive(() =>
        {
            Reevaluate();
            return Task.CompletedTask;
        });
    }

    private async Task ReshowAsync()
    {
        if (_active is not { } video || Status.State is not (VideoPlaybackState.Playing or VideoPlaybackState.Paused)) return;
        try
        {
            await ShowCoreAsync(video, CancellationToken.None);
        }
        catch (VideoHostException ex)
        {
            await ScheduleRecoveryOrFailAsync(new VideoHostFailure(ex.Message, ex.Recoverable));
        }
        catch (VideoHostBusyException ex)
        {
            await YieldToOtherInstanceAsync(video, ex);
        }
    }

    private void RunExclusive(Func<Task> work)
    {
        if (_disposed) return;
        _ = Run();

        async Task Run()
        {
            try
            {
                await _gate.WaitAsync();
                try
                {
                    if (!_disposed) await work();
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Updating the video wallpaper failed");
            }
        }
    }

    private static (string Target, WallpaperFitMode Fit, bool Fullscreen, bool Battery) Snapshot(AppSettings s) =>
        (s.VideoDisplayTarget, s.VideoFitMode, s.VideoPauseOnFullscreen, s.VideoPauseOnBattery);
}
