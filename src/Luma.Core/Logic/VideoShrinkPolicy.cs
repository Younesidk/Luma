using Luma.Core.Models;

namespace Luma.Core.Logic;

/// <summary>Decides whether an imported video is worth shrinking to the screens, and to what size and bitrate.</summary>
public static class VideoShrinkPolicy
{
    /// <summary>Shrink only when the screens need at most this fraction of the video's width/height (~2x the pixels or more).</summary>
    public const double MaxScale = 0.75;

    /// <summary>
    /// The size to re-encode to, or null to keep the video as it is. The result still covers the largest display in both
    /// directions (so "Fill" never upscales), keeps the aspect ratio and has even dimensions, as H.264 requires.
    /// </summary>
    public static (int Width, int Height)? TargetSize(int videoWidth, int videoHeight, IReadOnlyList<DisplayInfo> displays)
    {
        if (videoWidth <= 0 || videoHeight <= 0 || displays.Count == 0) return null;
        var screenWidth = displays.Max(d => d.Bounds.Width);
        var screenHeight = displays.Max(d => d.Bounds.Height);
        if (screenWidth <= 0 || screenHeight <= 0) return null;

        var scale = Math.Max((double)screenWidth / videoWidth, (double)screenHeight / videoHeight);
        if (scale > MaxScale) return null;

        var width = Even(Math.Ceiling(videoWidth * scale));
        var height = Even(Math.Ceiling(videoHeight * scale));
        return (Math.Max(width, 2), Math.Max(height, 2));

        static int Even(double value) => (int)value + ((int)value & 1);
    }

    /// <summary>
    /// Average H.264 bitrate for a looping wallpaper: 0.25 bits per pixel per frame (~15.5 Mbit/s at 1080p30, ~31 at 1080p60),
    /// 5–80 Mbit/s. Generous on purpose: the copy replaces a high-bitrate original and is watched full screen, and hardware
    /// encoders need more bits than a good software encoder for the same picture. Bitrate barely affects playback memory.
    /// </summary>
    public static uint Bitrate(int width, int height, double framesPerSecond)
    {
        var fps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? Math.Min(framesPerSecond, 120) : 30;
        return (uint)Math.Clamp(width * (double)height * fps * 0.25, 5_000_000, 80_000_000);
    }

    /// <summary>Peak bitrate for peak-constrained VBR: 1.5x the average, so detailed or fast scenes get the bits they need.</summary>
    public static uint PeakBitrate(uint averageBitrate) => (uint)Math.Min(averageBitrate * 1.5, 120_000_000);
}
