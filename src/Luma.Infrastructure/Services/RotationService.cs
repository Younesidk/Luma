using System.Globalization;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Services;

/// <summary>
/// Application-managed rotation: a single loop that sleeps until the next due time (no polling), wakes up when the
/// settings change and only rotates through wallpapers that are already on disk. It only runs while Luma is running.
/// </summary>
public sealed class RotationService : IRotationService
{
    public const string LastRotationKey = "rotation.last";
    public const string CurrentKey = "rotation.current";

    private readonly ISettingsService _settings;
    private readonly IDownloadLibrary _library;
    private readonly IWallpaperApplyService _applier;
    private readonly IStateStore _state;
    private readonly ILogger<RotationService> _logger;
    private readonly TimeProvider _time;
    private readonly IVideoWallpaperService? _video;
    private readonly object _lock = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private TaskCompletionSource _settingsChanged = NewSignal();

    public RotationService(ISettingsService settings, IDownloadLibrary library, IWallpaperApplyService applier,
        IStateStore state, ILogger<RotationService> logger, TimeProvider? time = null, IVideoWallpaperService? video = null)
    {
        _settings = settings;
        _library = library;
        _applier = applier;
        _state = state;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _video = video;
    }

    public event EventHandler? Rotated;

    public bool IsRunning => _loop is { IsCompleted: false };

    public DateTimeOffset? NextRotationAt { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning) return;
            _loopCts = new CancellationTokenSource();
            _settings.Changed += OnSettingsChanged;
            var ct = _loopCts.Token;
            _loop = Task.Run(() => RunAsync(ct));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_lock)
        {
            _settings.Changed -= OnSettingsChanged;
            _loopCts?.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            try { await loop; }
            catch (OperationCanceledException) { /* expected */ }
        }
        NextRotationAt = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _loopCts?.Dispose();
    }

    public async Task<ApplyResult> RotateNowAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var candidates = await _library.GetRotationCandidatesAsync(settings.RotationSource, settings.RotationCollectionId, cancellationToken);
        await _state.SetAsync(LastRotationKey, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), cancellationToken);

        if (candidates.Count == 0)
        {
            return ApplyResult.Fail(ApplyFailureReason.FileNotFound,
                "There are no downloaded wallpapers to rotate through yet. Download a few wallpapers first.");
        }

        var currentKey = await _state.GetAsync(CurrentKey, cancellationToken);
        var pick = RotationSelector.Pick(candidates, c => c.Wallpaper.Key, currentKey, settings.RotationRandom);
        if (pick is null) return ApplyResult.Fail(ApplyFailureReason.FileNotFound, "No wallpaper could be selected.");

        var result = await _applier.ApplyFileAsync(pick.FilePath, pick.Wallpaper, settings.DefaultFitMode, cancellationToken);
        if (result.Success)
        {
            await _state.SetAsync(CurrentKey, pick.Wallpaper.Key, cancellationToken);
            Rotated?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = _settings.Current;
                var signal = Volatile.Read(ref _settingsChanged);

                if (!settings.RotationEnabled)
                {
                    NextRotationAt = null;
                    await signal.Task.WaitAsync(ct);
                    continue;
                }

                var interval = TimeSpan.FromMinutes(settings.RotationIntervalMinutes);
                var last = await ReadLastRotationAsync(ct);
                var delay = RotationSelector.DelayUntilNext(last, interval, _time.GetUtcNow());
                NextRotationAt = _time.GetUtcNow() + delay;

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timer = Task.Delay(delay, _time, waitCts.Token);
                var completed = await Task.WhenAny(timer, signal.Task);
                await waitCts.CancelAsync();

                if (completed != timer || ct.IsCancellationRequested) continue; // settings changed: re-evaluate

                if (!_settings.Current.RotationEnabled) continue;
                if (_video?.IsActive == true)
                {
                    // A live wallpaper the user picked wins over the schedule. Record the slot so the loop waits a full
                    // interval again instead of firing immediately.
                    await _state.SetAsync(LastRotationKey, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), ct);
                    _logger.LogInformation("Scheduled rotation skipped: a video wallpaper is active");
                    continue;
                }
                var result = await RotateNowAsync(ct);
                if (!result.Success) _logger.LogInformation("Scheduled rotation skipped: {Message}", result.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die from one bad rotation; back off for a minute so failures cannot spin.
                _logger.LogError(ex, "Wallpaper rotation failed");
                try { await Task.Delay(TimeSpan.FromMinutes(1), _time, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<DateTimeOffset?> ReadLastRotationAsync(CancellationToken ct)
    {
        var text = await _state.GetAsync(LastRotationKey, ct);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var previous = Interlocked.Exchange(ref _settingsChanged, NewSignal());
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
