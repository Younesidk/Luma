using System.Text.Json;
using System.Text.Json.Serialization;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Services;

/// <summary>Persists <see cref="AppSettings"/> as settings.json with atomic writes and coalesced saves.</summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ILumaPaths _paths;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private Timer? _saveTimer;
    private bool _dirty;
    private AppSettings _current = new();

    public SettingsService(ILumaPaths paths, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public string EffectiveDownloadDirectory =>
        string.IsNullOrWhiteSpace(_current.DownloadDirectory) ? _paths.DefaultDownloadDirectory : _current.DownloadDirectory!;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = _paths.SettingsPath;
        AppSettings loaded = new();
        try
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged settings file must never stop the application from starting. Keep a copy for the user.
            _logger.LogWarning(ex, "Settings file could not be read; falling back to defaults");
            TryBackupCorruptFile(path);
        }

        loaded.Normalize();
        lock (_gate) _current = loaded;
    }

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(_current);
            _current.Normalize();
            _dirty = true;
            snapshot = _current;
            _saveTimer ??= new Timer(_ => _ = SaveSafelyAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke(this, snapshot);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        await SaveAsync(cancellationToken);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        AppSettings fresh = new();
        lock (_gate)
        {
            _current = fresh;
            _dirty = true;
        }
        Changed?.Invoke(this, fresh);
        await SaveAsync(cancellationToken);
    }

    private async Task SaveSafelyAsync()
    {
        try
        {
            await SaveAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            string json;
            lock (_gate)
            {
                if (!_dirty) return;
                json = JsonSerializer.Serialize(_current, JsonOptions);
                _dirty = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_paths.SettingsPath)!);
            var temp = _paths.SettingsPath + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, _paths.SettingsPath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void TryBackupCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // best effort only
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        _writeLock.Dispose();
    }
}
