using Luma.Core.Enums;
using Luma.Core.Models;

namespace Luma.Core.Logic;

/// <summary>A region of the video frame in 0..1 coordinates (what the player calls a normalised rectangle).</summary>
public readonly record struct NormalizedRect(float Left, float Top, float Right, float Bottom)
{
    public static NormalizedRect Full { get; } = new(0, 0, 1, 1);
}

/// <summary>Which part of the video is drawn where on one output surface. The rest of the surface is black.</summary>
public readonly record struct VideoFrameLayout(NormalizedRect Source, PixelRect Destination);

/// <summary>Geometry for video wallpapers. Pure functions, so every fit mode is unit tested without a GPU.</summary>
public static class VideoLayout
{
    /// <summary>Fit modes that make sense for video. Tile, Center and Span are image-only.</summary>
    public static readonly IReadOnlyList<WallpaperFitMode> SupportedFitModes = [WallpaperFitMode.Fill, WallpaperFitMode.Fit, WallpaperFitMode.Stretch];

    public static WallpaperFitMode Normalize(WallpaperFitMode fit) => SupportedFitModes.Contains(fit) ? fit : WallpaperFitMode.Fill;

    /// <summary>
    /// Computes the layout for a <paramref name="videoWidth"/>×<paramref name="videoHeight"/> video on an output surface.
    /// Fill crops the video (centred) so it covers the surface without distortion; Fit letterboxes it; Stretch distorts.
    /// </summary>
    public static VideoFrameLayout Compute(int videoWidth, int videoHeight, int surfaceWidth, int surfaceHeight, WallpaperFitMode fit)
    {
        var surface = new PixelRect(0, 0, Math.Max(surfaceWidth, 0), Math.Max(surfaceHeight, 0));
        if (videoWidth <= 0 || videoHeight <= 0 || surface.IsEmpty) return new VideoFrameLayout(NormalizedRect.Full, surface);

        var videoAspect = (double)videoWidth / videoHeight;
        var surfaceAspect = (double)surfaceWidth / surfaceHeight;

        switch (Normalize(fit))
        {
            case WallpaperFitMode.Stretch:
                return new VideoFrameLayout(NormalizedRect.Full, surface);

            case WallpaperFitMode.Fit:
                if (Math.Abs(videoAspect - surfaceAspect) < 1e-6) return new VideoFrameLayout(NormalizedRect.Full, surface);
                if (videoAspect > surfaceAspect)
                {
                    var height = Math.Clamp((int)Math.Round(surfaceWidth / videoAspect), 1, surfaceHeight);
                    return new VideoFrameLayout(NormalizedRect.Full, new PixelRect(0, (surfaceHeight - height) / 2, surfaceWidth, height));
                }
                else
                {
                    var width = Math.Clamp((int)Math.Round(surfaceHeight * videoAspect), 1, surfaceWidth);
                    return new VideoFrameLayout(NormalizedRect.Full, new PixelRect((surfaceWidth - width) / 2, 0, width, surfaceHeight));
                }

            default: // Fill
                if (Math.Abs(videoAspect - surfaceAspect) < 1e-6) return new VideoFrameLayout(NormalizedRect.Full, surface);
                if (videoAspect > surfaceAspect)
                {
                    var visible = (float)(surfaceAspect / videoAspect);
                    var margin = (1 - visible) / 2;
                    return new VideoFrameLayout(new NormalizedRect(margin, 0, 1 - margin, 1), surface);
                }
                else
                {
                    var visible = (float)(videoAspect / surfaceAspect);
                    var margin = (1 - visible) / 2;
                    return new VideoFrameLayout(new NormalizedRect(0, margin, 1, 1 - margin), surface);
                }
        }
    }

    /// <summary>Scales a frame to fit inside <paramref name="maxWidth"/> while keeping its aspect ratio (even dimensions, never upscaled).</summary>
    public static (int Width, int Height) ScaleToWidth(int width, int height, int maxWidth)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        if (width <= maxWidth) return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
        var scaledHeight = (int)Math.Round((double)height * maxWidth / width);
        return (maxWidth & ~1, Math.Max(2, scaledHeight & ~1));
    }

    /// <summary>
    /// Chooses the displays a video should cover. "all" (or nothing) means every display; a device name means that display.
    /// When the chosen display is not connected the main display is used, and <paramref name="fellBack"/> is set.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> ResolveDisplays(IReadOnlyList<DisplayInfo> displays, string? target, out bool fellBack)
    {
        fellBack = false;
        if (displays.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, AllDisplays, StringComparison.OrdinalIgnoreCase)) return displays;

        var match = displays.FirstOrDefault(d => string.Equals(d.DeviceName, target, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return [match];

        fellBack = true;
        return [displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0]];
    }

    public const string AllDisplays = "all";

    /// <summary>Position of a display inside the desktop window that hosts the wallpaper (whose top-left is <paramref name="parentOrigin"/> in screen coordinates).</summary>
    public static PixelRect ToParentCoordinates(PixelRect displayBounds, int parentOriginX, int parentOriginY) =>
        displayBounds.Offset(-parentOriginX, -parentOriginY);
}
