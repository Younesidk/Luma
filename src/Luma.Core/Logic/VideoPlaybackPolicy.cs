using Luma.Core.Enums;
using Luma.Core.Models;

namespace Luma.Core.Logic;

/// <summary>Decides whether the video wallpaper should be decoding. Pure, so every combination is unit tested.</summary>
public static class VideoPlaybackPolicy
{
    /// <param name="activeDisplays">Device names of the displays the video is shown on.</param>
    public static PauseReasons Evaluate(DesktopConditions conditions, bool userPaused, bool pauseOnFullscreen, bool pauseOnBattery,
        IReadOnlyCollection<string> activeDisplays)
    {
        var reasons = PauseReasons.None;
        if (userPaused) reasons |= PauseReasons.User;
        if (pauseOnFullscreen && conditions.FullscreenAppRunning) reasons |= PauseReasons.FullscreenApp;
        if (pauseOnBattery && conditions.OnBattery) reasons |= PauseReasons.OnBattery;
        if (conditions.SessionLocked) reasons |= PauseReasons.SessionLocked;
        if (conditions.DisplaysOff) reasons |= PauseReasons.DisplaysOff;
        if (conditions.Suspended) reasons |= PauseReasons.Suspended;

        // Nobody can see the video when every display showing it is covered, whatever the settings say.
        if (activeDisplays.Count > 0 && activeDisplays.All(conditions.CoveredDisplays.Contains)) reasons |= PauseReasons.DesktopHidden;
        return reasons;
    }

    /// <summary>The most relevant reason, phrased for the status line.</summary>
    public static string Describe(PauseReasons reasons)
    {
        if (reasons == PauseReasons.None) return "Playing";
        if (reasons.HasFlag(PauseReasons.User)) return "Paused";
        if (reasons.HasFlag(PauseReasons.Suspended)) return "Paused while the computer sleeps";
        if (reasons.HasFlag(PauseReasons.SessionLocked)) return "Paused while Windows is locked";
        if (reasons.HasFlag(PauseReasons.DisplaysOff)) return "Paused while the screen is off";
        if (reasons.HasFlag(PauseReasons.FullscreenApp)) return "Paused while a fullscreen app is running";
        if (reasons.HasFlag(PauseReasons.OnBattery)) return "Paused on battery power";
        return "Paused while the desktop is hidden";
    }
}

/// <summary>A visible top-level window that may hide the desktop, as gathered by the platform layer.</summary>
public readonly record struct WindowSnapshot(PixelRect Bounds, bool IsMaximized, string? Description = null);

/// <summary>Works out which displays are hidden behind fullscreen or maximised windows.</summary>
public static class DesktopOcclusion
{
    public sealed record Result(bool FullscreenAppRunning, IReadOnlySet<string> CoveredDisplays);

    /// <summary>
    /// Any window covering a display's work area hides its desktop. A <i>non-maximised</i> window covering the display's
    /// full bounds is a fullscreen app (game, video player, presentation). Maximised windows are never "fullscreen", even
    /// when an auto-hiding taskbar makes the work area equal to the whole display.
    /// </summary>
    public static Result Evaluate(IReadOnlyList<WindowSnapshot> windows, IReadOnlyList<DisplayInfo> displays, bool systemReportsFullscreen = false)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullscreen = systemReportsFullscreen;
        foreach (var display in displays)
        {
            foreach (var window in windows)
            {
                if (!window.IsMaximized && window.Bounds.Contains(display.Bounds)) fullscreen = true;
                if (window.Bounds.Contains(display.WorkArea)) covered.Add(display.DeviceName);
            }
        }
        return new Result(fullscreen, covered);
    }

    /// <summary>
    /// Short-lived system windows (wallpaper transitions, overlays) can briefly cover a display. A display only counts
    /// as covered, and an app as fullscreen, when two consecutive samples agree. Uncovering takes effect at once.
    /// </summary>
    public static Result Confirm(Result? previous, Result current)
    {
        if (previous is null) return new Result(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var covered = new HashSet<string>(current.CoveredDisplays.Where(previous.CoveredDisplays.Contains), StringComparer.OrdinalIgnoreCase);
        return new Result(previous.FullscreenAppRunning && current.FullscreenAppRunning, covered);
    }
}

/// <summary>
/// Retry schedule for playback failures: a few attempts with growing delays, so a crashing driver or a damaged file
/// can never make Luma spin. Playing successfully for <see cref="StableAfter"/> resets the count.
/// </summary>
public sealed class VideoRecoveryPolicy
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(1);

    private int _attempts;
    private DateTimeOffset? _playingSince;

    public int Attempts => _attempts;

    public int MaxAttempts => Delays.Length;

    /// <summary>Call when playback started (or resumed) successfully.</summary>
    public void OnPlaying(DateTimeOffset now) => _playingSince ??= now;

    /// <summary>Returns the delay before the next attempt, or null when Luma should give up.</summary>
    public TimeSpan? NextDelay(DateTimeOffset now)
    {
        if (_playingSince is { } since && now - since >= StableAfter) _attempts = 0;
        _playingSince = null;
        if (_attempts >= Delays.Length) return null;
        return Delays[_attempts++];
    }

    public void Reset()
    {
        _attempts = 0;
        _playingSince = null;
    }
}
