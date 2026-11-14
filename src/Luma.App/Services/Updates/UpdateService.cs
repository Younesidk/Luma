using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Luma.App.Services.Updates;

public interface IUpdateService
{
    UpdateSnapshot Current { get; }

    /// <summary>Raised on any thread whenever <see cref="Current"/> changes.</summary>
    event EventHandler<UpdateSnapshot>? Changed;

    /// <summary>Raised after Velopack's updater has been started; the app must now shut down so it can install.</summary>
    event EventHandler? RestartRequested;

    bool IsSupported { get; }

    /// <summary>True when the user chose "Later" for the release currently on offer.</summary>
    bool IsPostponed { get; }

    Task<UpdateCheckOutcome> CheckAsync(bool userInitiated = false, CancellationToken cancellationToken = default);

    /// <summary>Downloads the release found by the last check. Returns true when it is ready to install.</summary>
    Task<bool> DownloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels a download in progress, if any.</summary>
    void CancelDownload();

    /// <summary>Hands over to Velopack's updater and asks the app to exit. Returns false if that couldn't start.</summary>
    bool RestartToApply();

    /// <summary>Hides the offer for this release until the user checks manually or Luma starts again.</summary>
    void Postpone();
}

/// <summary>
/// Luma's update state machine on top of Velopack. Every operation is guarded so only one check or download runs at a
/// time, every failure is caught and turned into state, and nothing here can throw into start-up or the UI.
/// </summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    private readonly IUpdateClient _client;
    private readonly LumaUpdateOptions _options;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _downloadCts;
    private UpdateSnapshot _current;
    private string? _postponedVersion;
    private Task? _background;

    public UpdateService(IUpdateClient client, IOptions<LumaUpdateOptions> options, ILogger<UpdateService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
        _current = new UpdateSnapshot(UpdateStatus.Idle, SafeCurrentVersion(), null, 0, UpdateOperation.None, null, null);

        if (!_options.Enabled || !SafeIsSupported())
        {
            _current = _current with { Status = UpdateStatus.Unsupported };
        }
        else if (SafePendingRestart() is { } pending)
        {
            // Downloaded in an earlier session but not installed yet (normally Velopack installs these on start-up).
            _current = _current with { Status = UpdateStatus.ReadyToRestart, Release = pending, Progress = 100 };
        }
    }

    public UpdateSnapshot Current
    {
        get { lock (_stateLock) return _current; }
    }

    public event EventHandler<UpdateSnapshot>? Changed;

    public event EventHandler? RestartRequested;

    public bool IsSupported => _options.Enabled && SafeIsSupported();

    public bool IsPostponed
    {
        get
        {
            lock (_stateLock) return _postponedVersion is not null && _postponedVersion == _current.Release?.Version;
        }
    }

    /// <summary>Starts the quiet background schedule: one check shortly after start-up, then every few hours.</summary>
    public void StartBackgroundChecks()
    {
        if (_background is not null || !IsSupported) return;
        _background = Task.Run(() => RunBackgroundChecksAsync(_lifetime.Token));
    }

    private async Task RunBackgroundChecksAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds)), token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                await CheckAsync(userInitiated: false, token).ConfigureAwait(false);
                if (_options.CheckIntervalHours <= 0) return;
                await Task.Delay(TimeSpan.FromHours(_options.CheckIntervalHours), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background update checks stopped unexpectedly");
        }
    }

    public async Task<UpdateCheckOutcome> CheckAsync(bool userInitiated = false, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return UpdateCheckOutcome.Disabled;
        if (!SafeIsSupported())
        {
            Publish(s => s with { Status = UpdateStatus.Unsupported });
            return UpdateCheckOutcome.Unsupported;
        }

        // Never queue: if a check or download is already running, the caller just sees "busy".
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return UpdateCheckOutcome.Busy;
        try
        {
            var before = Current;
            // An update that is downloaded and waiting for a restart stays that way; checking again adds nothing.
            if (before.Status is UpdateStatus.ReadyToRestart or UpdateStatus.Restarting)
            {
                if (userInitiated) ClearPostponement();
                return UpdateCheckOutcome.UpdateAvailable;
            }

            Publish(s => s with { Status = UpdateStatus.Checking, FailedOperation = UpdateOperation.None, Error = null });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.CheckTimeoutSeconds)));

            UpdateRelease? release;
            try
            {
                release = await _client.CheckForUpdateAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
                Publish(_ => before);
                return UpdateCheckOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                var message = DescribeFailure(ex, UpdateOperation.Check);
                // Background failures are expected (offline laptop, GitHub hiccup); keep them out of the warning noise.
                if (userInitiated) _logger.LogWarning(ex, "Update check failed");
                else _logger.LogInformation("Background update check failed: {Reason}", ex.Message);
                Publish(s => s with { Status = UpdateStatus.Failed, FailedOperation = UpdateOperation.Check, Error = message, LastCheckedAt = DateTimeOffset.Now });
                return UpdateCheckOutcome.Failed;
            }

            var current = SafeCurrentVersion();
            if (release is null || !UpdateVersioning.IsNewerStable(release.Version, current))
            {
                if (release is not null)
                    _logger.LogInformation("Ignoring update {Version}: not a newer stable version than {Current}", release.Version, current);
                Publish(s => s with { Status = UpdateStatus.UpToDate, CurrentVersion = current, Release = null, Progress = 0, LastCheckedAt = DateTimeOffset.Now });
                return UpdateCheckOutcome.UpToDate;
            }

            if (userInitiated) ClearPostponement();
            _logger.LogInformation("Update available: {Current} -> {Version}", current, release.Version);
            Publish(s => s with { Status = UpdateStatus.Available, CurrentVersion = current, Release = release, Progress = 0, LastCheckedAt = DateTimeOffset.Now });
            return UpdateCheckOutcome.UpdateAvailable;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return false;
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return false;
        try
        {
            var snapshot = Current;
            if (snapshot.Status == UpdateStatus.ReadyToRestart) return true;
            // A failed download can be retried with the same release; anything else needs an available release.
            var retryable = snapshot.Status == UpdateStatus.Failed && snapshot.FailedOperation == UpdateOperation.Download;
            if (snapshot.Release is not { } release || (snapshot.Status != UpdateStatus.Available && !retryable)) return false;

            ClearPostponement();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            Interlocked.Exchange(ref _downloadCts, cts);
            Publish(s => s with { Status = UpdateStatus.Downloading, Progress = 0, FailedOperation = UpdateOperation.None, Error = null });

            try
            {
                var lastReported = -1;
                await _client.DownloadAsync(release, percent =>
                {
                    var clamped = Math.Clamp(percent, 0, 100);
                    if (clamped == Volatile.Read(ref lastReported)) return;
                    Volatile.Write(ref lastReported, clamped);
                    Publish(s => s.Status == UpdateStatus.Downloading ? s with { Progress = clamped } : s);
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _logger.LogInformation("Update download of {Version} was cancelled", release.Version);
                Publish(s => s with { Status = UpdateStatus.Available, Progress = 0 });
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Downloading update {Version} failed", release.Version);
                Publish(s => s with { Status = UpdateStatus.Failed, FailedOperation = UpdateOperation.Download, Error = DescribeFailure(ex, UpdateOperation.Download), Progress = 0 });
                return false;
            }
            finally
            {
                Interlocked.CompareExchange(ref _downloadCts, null, cts);
            }

            _logger.LogInformation("Update {Version} downloaded and verified", release.Version);
            Publish(s => s with { Status = UpdateStatus.ReadyToRestart, Progress = 100 });
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void CancelDownload()
    {
        try
        {
            Volatile.Read(ref _downloadCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download finished in the meantime.
        }
    }

    public bool RestartToApply()
    {
        var snapshot = Current;
        if (snapshot.Status != UpdateStatus.ReadyToRestart || snapshot.Release is not { } release) return false;

        try
        {
            _logger.LogInformation("Restarting to install update {Version}", release.Version);
            _client.ApplyOnExitAndRestart(release);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start the updater for {Version}", release.Version);
            Publish(s => s with { Status = UpdateStatus.Failed, FailedOperation = UpdateOperation.Apply, Error = DescribeFailure(ex, UpdateOperation.Apply) });
            return false;
        }

        Publish(s => s with { Status = UpdateStatus.Restarting });
        RestartRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Postpone()
    {
        lock (_stateLock) _postponedVersion = _current.Release?.Version;
        Publish(s => s);
    }

    private void ClearPostponement()
    {
        lock (_stateLock) _postponedVersion = null;
    }

    private void Publish(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        UpdateSnapshot next;
        lock (_stateLock)
        {
            next = change(_current);
            _current = next;
        }
        try
        {
            Changed?.Invoke(this, next);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An update state listener failed");
        }
    }

    public static string DescribeFailure(Exception ex, UpdateOperation operation)
    {
        if (IsConnectivityFailure(ex))
        {
            return operation == UpdateOperation.Download
                ? "The download was interrupted. Check your internet connection and try again."
                : "Luma couldn't reach the update server. Check your internet connection and try again later.";
        }
        return operation switch
        {
            UpdateOperation.Download => "The update couldn't be downloaded or didn't pass verification, so nothing was changed. Try again later.",
            UpdateOperation.Apply => "The updater couldn't be started. Luma is unchanged; try again or reinstall the latest version.",
            _ => "Luma couldn't check for updates right now. Try again later."
        };
    }

    private static bool IsConnectivityFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or SocketException or TimeoutException or TaskCanceledException or OperationCanceledException) return true;
        }
        return false;
    }

    private bool SafeIsSupported()
    {
        try { return _client.IsSupported; }
        catch { return false; }
    }

    private string? SafeCurrentVersion()
    {
        try { return _client.CurrentVersion; }
        catch { return null; }
    }

    private UpdateRelease? SafePendingRestart()
    {
        try { return _client.PendingRestart; }
        catch { return null; }
    }

    // The token source is cancelled but deliberately not disposed: a check finishing during shutdown may still read its token.
    public void Dispose() => _lifetime.Cancel();
}
