using System.Globalization;

namespace Luma.Core.Models;

/// <summary>Pixel dimensions of a wallpaper.</summary>
public readonly record struct WallpaperResolution(int Width, int Height)
{
    private static readonly (int W, int H)[] CommonRatios =
    [
        (16, 9), (16, 10), (21, 9), (32, 9), (4, 3), (3, 2), (5, 4), (1, 1), (9, 16), (10, 16), (3, 4), (2, 3)
    ];

    public bool IsValid => Width > 0 && Height > 0;

    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;

    public long Pixels => (long)Width * Height;

    /// <summary>Human friendly "1920 × 1080".</summary>
    public string Label => IsValid ? $"{Width} × {Height}" : "Unknown";

    /// <summary>Aspect ratio label such as "16:9"; falls back to a decimal when it is not a common ratio.</summary>
    public string AspectLabel
    {
        get
        {
            if (!IsValid) return "Unknown";
            var ratio = AspectRatio;
            foreach (var (w, h) in CommonRatios)
            {
                if (Math.Abs(ratio - (double)w / h) < 0.02) return $"{w}:{h}";
            }
            return ratio.ToString("0.##", CultureInfo.InvariantCulture) + ":1";
        }
    }

    public static WallpaperResolution? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('x', 'X', '×');
        if (parts.Length != 2) return null;
        return int.TryParse(parts[0].Trim(), out var w) && int.TryParse(parts[1].Trim(), out var h) && w > 0 && h > 0
            ? new WallpaperResolution(w, h)
            : null;
    }

    public override string ToString() => IsValid ? $"{Width}x{Height}" : "unknown";
}
