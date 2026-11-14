using Velopack;

namespace Luma.App.Services.Updates;

/// <summary>Where Luma looks for new versions. Bound from the "Luma:Updates" section of appsettings.json.</summary>
public sealed class LumaUpdateOptions
{
    public const string SectionName = "Luma:Updates";

    /// <summary>Turns the whole update system off (it is also inert in development builds that Velopack didn't install).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A GitHub repository URL (uses the public GitHub Releases API, stable releases only), or a local folder / plain
    /// HTTP(S) URL that contains Velopack release files, which is how updates are tested locally without GitHub.
    /// Override with the LUMA_Luma__Updates__Source environment variable.
    /// </summary>
    public string Source { get; set; } = "https://github.com/Younesidk/Luma";

    /// <summary>How long after the window opens the first background check runs, so start-up stays quick.</summary>
    public int InitialDelaySeconds { get; set; } = 20;

    /// <summary>How often a long-running Luma checks again. 0 disables periodic checks.</summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>Upper bound for a single check, so a hanging network never leaves the UI stuck in "Checking".</summary>
    public int CheckTimeoutSeconds { get; set; } = 30;
}

public enum UpdateStatus
{
    /// <summary>No check has run yet.</summary>
    Idle,
    /// <summary>This copy of Luma wasn't installed by Velopack (e.g. a development build), so it cannot update itself.</summary>
    Unsupported,
    Checking,
    UpToDate,
    Available,
    Downloading,
    /// <summary>The update is downloaded and verified; restarting Luma installs it.</summary>
    ReadyToRestart,
    Restarting,
    Failed
}

/// <summary>The operation that last failed, so the UI can tell a failed check from a failed download.</summary>
public enum UpdateOperation
{
    None,
    Check,
    Download,
    Apply
}

/// <summary>A release Luma can update to. Wraps Velopack's asset so the rest of the app doesn't depend on Velopack types.</summary>
public sealed record UpdateRelease(string Version, string? ReleaseNotes, long SizeBytes)
{
    /// <summary>The underlying Velopack update, kept so download/apply operate on exactly what the check returned.</summary>
    internal object? Native { get; init; }
}

public sealed record UpdateSnapshot(
    UpdateStatus Status,
    string? CurrentVersion,
    UpdateRelease? Release,
    int Progress,
    UpdateOperation FailedOperation,
    string? Error,
    DateTimeOffset? LastCheckedAt);

public enum UpdateCheckOutcome
{
    UpdateAvailable,
    UpToDate,
    /// <summary>Another check or download is already running; nothing was started.</summary>
    Busy,
    Unsupported,
    Disabled,
    Failed,
    Cancelled
}

/// <summary>Version rules for the stable channel.</summary>
public static class UpdateVersioning
{
    /// <summary>True when <paramref name="candidate"/> is a valid, stable (non-prerelease) version strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewerStable(string? candidate, string? current)
    {
        if (!SemanticVersion.TryParse(candidate ?? string.Empty, out var next) || next is null) return false;
        if (next.IsPrerelease) return false;
        // Without a known current version we can't prove the release is newer; never offer what might be a downgrade.
        if (!SemanticVersion.TryParse(current ?? string.Empty, out var now) || now is null) return false;
        return next > now;
    }
}
