using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;

namespace Luma.App.Services.Updates;

/// <summary>
/// The narrow surface <see cref="UpdateService"/> needs from Velopack. Keeping it this small lets the update logic be
/// tested with a fake and keeps Velopack types out of the view models.
/// </summary>
public interface IUpdateClient
{
    /// <summary>True when this process runs from a Velopack installation and can update itself.</summary>
    bool IsSupported { get; }

    /// <summary>The installed version, or the assembly version for development builds.</summary>
    string? CurrentVersion { get; }

    /// <summary>An update that was already downloaded and only needs a restart, if any.</summary>
    UpdateRelease? PendingRestart { get; }

    /// <summary>Returns the newest release on the stable channel, or null when Luma is up to date.</summary>
    Task<UpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken);

    /// <summary>Downloads (delta packages when possible, else the full package) and verifies the checksum.</summary>
    Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Starts Velopack's updater, which waits for Luma to exit, installs <paramref name="release"/> and relaunches Luma.
    /// The caller must then shut the application down normally.
    /// </summary>
    void ApplyOnExitAndRestart(UpdateRelease release);
}

public sealed class VelopackUpdateClient : IUpdateClient
{
    private readonly UpdateManager? _manager;
    private readonly ILogger<VelopackUpdateClient> _logger;

    public VelopackUpdateClient(IOptions<LumaUpdateOptions> options, ILogger<VelopackUpdateClient> logger)
    {
        _logger = logger;
        var source = options.Value.Source?.Trim();
        if (string.IsNullOrEmpty(source)) return;

        try
        {
            // Stable channel only: prereleases are never offered. Downgrades are refused.
            var updateOptions = new UpdateOptions { AllowVersionDowngrade = false };
            _manager = IsGitHubRepository(source)
                // Public repository: no token. Never embed credentials in the app.
                ? new UpdateManager(new GithubSource(source, accessToken: null, prerelease: false), updateOptions)
                // Local folder or plain web server holding Velopack release files (used for local update testing).
                : new UpdateManager(source, updateOptions);
        }
        catch (Exception ex)
        {
            // A misconfigured source must never affect start-up; updates simply become unavailable.
            _logger.LogWarning(ex, "The update manager could not be created for source {Source}", source);
            _manager = null;
        }
    }

    public bool IsSupported
    {
        get
        {
            try
            {
                return _manager is { IsInstalled: true };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not determine whether Luma is a Velopack installation");
                return false;
            }
        }
    }

    public string? CurrentVersion
    {
        get
        {
            try
            {
                if (_manager?.CurrentVersion is { } installed) return installed.ToNormalizedString();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the installed version");
            }
            return AppVersion.Current;
        }
    }

    public UpdateRelease? PendingRestart
    {
        get
        {
            try
            {
                return _manager is { IsInstalled: true, UpdatePendingRestart: { } asset } ? ToRelease(asset, null) : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not look for an already downloaded update");
                return null;
            }
        }
    }

    public async Task<UpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        var manager = RequireInstalled();
        // Velopack's check takes no token; WaitAsync stops us waiting on it when cancelled or timed out.
        var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (info is null) return null;
        return ToRelease(info.TargetFullRelease, info);
    }

    public Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        var manager = RequireInstalled();
        if (release.Native is not UpdateInfo info)
            throw new InvalidOperationException("The update must come from a check performed by this client.");
        return manager.DownloadUpdatesAsync(info, progress, cancellationToken);
    }

    public void ApplyOnExitAndRestart(UpdateRelease release)
    {
        var manager = RequireInstalled();
        var asset = release.Native switch
        {
            UpdateInfo info => info.TargetFullRelease,
            VelopackAsset a => a,
            _ => null
        };
        // Not silent: Velopack shows its own small progress window while it swaps the files, then restarts Luma.
        manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true);
    }

    private UpdateManager RequireInstalled() =>
        _manager is { IsInstalled: true } manager
            ? manager
            : throw new InvalidOperationException("Luma was not installed with Velopack, so it cannot update itself.");

    private static UpdateRelease ToRelease(VelopackAsset asset, UpdateInfo? info) =>
        new(asset.Version.ToNormalizedString(), string.IsNullOrWhiteSpace(asset.NotesMarkdown) ? null : asset.NotesMarkdown, asset.Size)
        {
            Native = (object?)info ?? asset
        };

    public static bool IsGitHubRepository(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
}

public static class AppVersion
{
    /// <summary>The version stamped into the assembly from Directory.Build.props (without the source-link "+hash" suffix).</summary>
    public static string Current { get; } =
        (typeof(AppVersion).Assembly
             .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
             .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
             .FirstOrDefault()?.InformationalVersion ?? "1.0.0")
        .Split('+')[0];
}
