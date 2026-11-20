using System.Runtime.Versioning;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>
/// Windows implementation of the live wallpaper: one decoder (<see cref="VideoRenderer"/>) feeding one window per display
/// (<see cref="DesktopWindowThread"/>) placed behind the desktop icons. Rebuilds its windows when Explorer restarts,
/// after resume and when displays change, and reports power/lock/fullscreen conditions. A named mutex guarantees that
/// only one Luma process puts video windows on the desktop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopVideoHost : IDesktopVideoHost
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShellWaitLimit = TimeSpan.FromSeconds(45);
    private const string InstanceMutexName = @"Local\Luma.LiveWallpaper.Host";

    private readonly ILogger<WindowsDesktopVideoHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _conditionsLock = new();
    private DesktopWindowThread? _windows;
    private VideoRenderer? _renderer;
    private string? _loadedPath;
    private VideoPresentation? _presentation;
    private bool _paused;
    private Mutex? _instanceMutex;
    private DesktopConditions _conditions = DesktopConditions.Normal;
    private DesktopOcclusion.Result? _lastOcclusionSample;
    private CancellationTokenSource? _rebuildCts;
    private VideoHostCapabilities? _capabilities;
    private bool _disposed;

    public WindowsDesktopVideoHost(ILogger<WindowsDesktopVideoHost> logger)
    {
        _logger = logger;
    }

    public VideoHostCapabilities Capabilities => _capabilities ??= WindowsVideoSupport.Check();

    public DesktopConditions Conditions
    {
        get { lock (_conditionsLock) return _conditions; }
    }

    public event EventHandler<DesktopConditions>? ConditionsChanged;
    public event EventHandler<VideoHostFailure>? Failed;
    public event EventHandler? DisplaysChanged;

    /// <summary>Frames presented by the current renderer (diagnostics and integration tests).</summary>
    public long FramesPresented => _renderer?.FramesPresented ?? 0;

    public IReadOnlyList<DisplayInfo> GetDisplays() => DesktopShell.GetDisplays();

    public async Task ShowAsync(VideoPresentation presentation, bool startPaused, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Capabilities.IsSupported) throw new VideoHostException(Capabilities.Notes ?? "Live wallpapers aren't supported here.", recoverable: false);

        await _gate.WaitAsync(cancellationToken);
        try
        {

            var windows = EnsureWindowThread();
            // The mutex is thread-affine: it is always taken and released on the long-lived window thread.
            await windows.InvokeAsync(AcquireInstanceMutex);
            CancelRebuild();

            if (_renderer is null || !string.Equals(_loadedPath, presentation.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                await ReleaseRendererAsync();
                var renderer = new VideoRenderer(_logger, "Luma video wallpaper");
                renderer.Failed += OnRendererFailed;
                _renderer = renderer;
                _loadedPath = presentation.FilePath;
                try
                {
                    await renderer.LoadAsync(presentation.FilePath).WaitAsync(LoadTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new VideoHostException("Windows took too long to open the video.");
                }
            }

            _paused = startPaused;
            _presentation = presentation;
            await AttachOutputsAsync(windows, presentation, cancellationToken);
            await windows.InvokeAsync(() => windows.SetOcclusionPolling(true));
            UpdateOcclusion();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never leave half a wallpaper on the desktop.
            await HideCoreAsync();
            if (ex is VideoHostException or VideoHostBusyException) throw;
            _logger.LogError(ex, "Showing the video wallpaper failed");
            throw new VideoHostException("The live wallpaper couldn't be started.", recoverable: true, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _renderer?.SetPlaying(!paused);
    }

    public async Task HideAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await HideCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await HideAsync();
        _disposed = true;
        _windows?.Dispose();
        _windows = null;
        _gate.Dispose();
    }

    // ----- internals (gate held) -----

    private async Task AttachOutputsAsync(DesktopWindowThread windows, VideoPresentation presentation, CancellationToken ct)
    {
        var renderer = _renderer!;
        var displays = GetDisplays().Where(d => presentation.DisplayDeviceNames.Contains(d.DeviceName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (displays.Count == 0) throw new VideoHostException("The selected display isn't connected.");

        await renderer.SetTargetsAsync([]); // release the old swap chains before their windows go away
        var outputs = await windows.InvokeAsync(() => windows.CreateOutputs(displays));
        await renderer.SetTargetsAsync(outputs.Select(o => new RenderTarget(o.Window, o.Width, o.Height, VideoLayout.Normalize(presentation.Fit))).ToList());
        renderer.SetPlaying(!_paused);
        try
        {
            await renderer.WaitForFrameAsync().WaitAsync(FirstFrameTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new VideoHostException("The video didn't produce a picture in time.");
        }
        await windows.InvokeAsync(windows.ShowOutputs);
    }

    private async Task HideCoreAsync()
    {
        CancelRebuild();
        _presentation = null;
        _lastOcclusionSample = null;
        await ReleaseRendererAsync();
        if (_windows is { } windows)
        {
            try
            {
                await windows.InvokeAsync(() =>
                {
                    windows.SetOcclusionPolling(false);
                    windows.DestroyOutputs();
                    ReleaseInstanceMutex();
                });
            }
            catch (VideoHostException ex)
            {
                _logger.LogWarning(ex, "The desktop windows couldn't be removed cleanly");
            }
        }
    }

    private Task ReleaseRendererAsync()
    {
        var renderer = _renderer;
        _renderer = null;
        _loadedPath = null;
        if (renderer is null) return Task.CompletedTask;
        renderer.Failed -= OnRendererFailed;
        // Dispose joins the render thread (bounded); keep that off the caller's thread.
        return Task.Run(renderer.Dispose);
    }

    private DesktopWindowThread EnsureWindowThread()
    {
        if (_windows is not null) return _windows;
        var windows = new DesktopWindowThread(_logger);
        windows.ShellRestarted += () => ScheduleRebuild("Explorer restarted", TimeSpan.FromMilliseconds(500));
        windows.OutputLost += () => ScheduleRebuild("the desktop window was recreated", TimeSpan.FromSeconds(1));
        windows.DisplaysChanged += () => DisplaysChanged?.Invoke(this, EventArgs.Empty);
        windows.OcclusionTick += UpdateOcclusion;
        windows.PowerSourceChanged += onBattery => UpdateConditions(c => c with { OnBattery = onBattery });
        windows.DisplayPowerChanged += off => UpdateConditions(c => c with { DisplaysOff = off });
        windows.SessionLockChanged += locked => UpdateConditions(c => c with { SessionLocked = locked });
        windows.SuspendChanged += suspended =>
        {
            UpdateConditions(c => c with { Suspended = suspended });
            if (!suspended) ScheduleRebuild("the computer resumed", TimeSpan.FromSeconds(2), onlyIfBroken: true);
        };
        _windows = windows;
        UpdateConditions(c => c with { OnBattery = DesktopShell.IsOnBattery() });
        return windows;
    }

    private void AcquireInstanceMutex()
    {
        if (_instanceMutex is not null) return;
        var mutex = new Mutex(false, InstanceMutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // a previous Luma crashed; its windows died with it
        }
        if (!owned)
        {
            mutex.Dispose();
            throw new VideoHostBusyException("Another copy of Luma is already showing a live wallpaper. Close it first.");
        }
        _instanceMutex = mutex;
    }

    private void ReleaseInstanceMutex()
    {
        var mutex = _instanceMutex;
        _instanceMutex = null;
        if (mutex is null) return;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (never acquired on this thread); closing the handle below is enough.
        }
        mutex.Dispose();
    }

    // ----- recovery -----

    private void ScheduleRebuild(string reason, TimeSpan initialDelay, bool onlyIfBroken = false)
    {
        if (_presentation is null || _disposed) return;
        _logger.LogInformation("Rebuilding the live wallpaper windows: {Reason}", reason);
        CancelRebuild();
        var cts = _rebuildCts = new CancellationTokenSource();
        _ = RebuildAsync(initialDelay, onlyIfBroken, cts.Token);
    }

    private async Task RebuildAsync(TimeSpan initialDelay, bool onlyIfBroken, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var delay = initialDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct);
                await _gate.WaitAsync(ct);
                try
                {
                    if (_presentation is not { } presentation || _windows is not { } windows || _renderer is null) return;
                    if (onlyIfBroken && await windows.InvokeAsync(windows.OutputsAreHealthy)) return;
                    await AttachOutputsAsync(windows, presentation, ct);
                    _logger.LogInformation("Live wallpaper windows rebuilt");
                    return;
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (VideoHostException ex) when (DateTime.UtcNow - started < ShellWaitLimit)
            {
                // Explorer usually needs a few seconds before its desktop exists again.
                _logger.LogDebug(ex, "Desktop not ready yet; retrying");
                delay = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The live wallpaper windows couldn't be rebuilt");
                Failed?.Invoke(this, new VideoHostFailure("Windows' desktop couldn't be found again after it was recreated.", Recoverable: true));
                return;
            }
        }
    }

    private void CancelRebuild()
    {
        _rebuildCts?.Cancel();
        _rebuildCts?.Dispose();
        _rebuildCts = null;
    }

    private void OnRendererFailed(VideoHostFailure failure)
    {
        _logger.LogWarning("Live wallpaper playback failed: {Message} (recoverable: {Recoverable})", failure.Message, failure.Recoverable);
        // The renderer is unusable now; the next ShowAsync loads a fresh one.
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_renderer is not null) await ReleaseRendererAsync();
            }
            finally
            {
                _gate.Release();
            }
            Failed?.Invoke(this, failure);
        });
    }

    // ----- conditions -----

    private void UpdateOcclusion()
    {
        var presentation = _presentation;
        if (presentation is null) return;
        var displays = GetDisplays().Where(d => presentation.DisplayDeviceNames.Contains(d.DeviceName, StringComparer.OrdinalIgnoreCase)).ToList();
        var windows = DesktopShell.GetCoveringWindowCandidates();
        var systemFullscreen = DesktopShell.SystemReportsFullscreen();
        var sample = DesktopOcclusion.Evaluate(windows, displays, systemFullscreen);
        var result = DesktopOcclusion.Confirm(Interlocked.Exchange(ref _lastOcclusionSample, sample), sample);
        if (result.FullscreenAppRunning && !Conditions.FullscreenAppRunning)
        {
            // Say which window made Luma pause, so a wrong detection can be diagnosed from the log.
            var culprits = windows.Where(w => !w.IsMaximized && displays.Any(d => w.Bounds.Contains(d.Bounds))).Select(w => w.Description).ToList();
            _logger.LogInformation("Fullscreen app detected: {Windows}{System}", culprits.Count > 0 ? string.Join("; ", culprits) : "(no window)",
                systemFullscreen ? " (Windows reports a fullscreen/presentation state)" : string.Empty);
        }
        UpdateConditions(c => c with { FullscreenAppRunning = result.FullscreenAppRunning, CoveredDisplays = result.CoveredDisplays });
    }

    private void UpdateConditions(Func<DesktopConditions, DesktopConditions> change)
    {
        DesktopConditions next;
        lock (_conditionsLock)
        {
            var previous = _conditions;
            next = change(previous);
            if (Same(previous, next)) return;
            _conditions = next;
        }
        ConditionsChanged?.Invoke(this, next);
    }

    private static bool Same(DesktopConditions a, DesktopConditions b) =>
        a.OnBattery == b.OnBattery && a.FullscreenAppRunning == b.FullscreenAppRunning && a.SessionLocked == b.SessionLocked
        && a.DisplaysOff == b.DisplaysOff && a.Suspended == b.Suspended && a.CoveredDisplays.SetEquals(b.CoveredDisplays);
}
