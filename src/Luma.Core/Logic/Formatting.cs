using System.Globalization;
using System.Text;
using Luma.Core.Models;

namespace Luma.Core.Logic;

public static class ByteSizeFormatter
{
    public static string Format(long? bytes)
    {
        if (bytes is null or < 0) return "Unknown";
        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

public static class SafeFileName
{
    private const int MaxStemLength = 80;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Removes characters that are invalid on common file systems and trims the result.</summary>
    public static string Sanitize(string? text, string fallback = "wallpaper")
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        var lastWasSeparator = false;
        foreach (var ch in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0 || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                if (!lastWasSeparator && sb.Length > 0) sb.Append('-');
                lastWasSeparator = true;
                continue;
            }
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
            lastWasSeparator = false;
        }

        var result = sb.ToString().Trim(' ', '.', '-', '_');
        if (result.Length > MaxStemLength) result = result[..MaxStemLength].TrimEnd(' ', '.', '-', '_');
        if (result.Length == 0) return fallback;
        if (ReservedNames.Contains(result)) result = "_" + result;
        return result;
    }

    /// <summary>Builds a descriptive, collision free download file name inside <paramref name="directory"/>.</summary>
    public static string BuildDownloadName(Wallpaper wallpaper, string extension, string directory, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        extension = extension.StartsWith('.') ? extension : "." + extension;
        var idPart = Sanitize(wallpaper.ExternalId, "id");
        if (idPart.Length > 24) idPart = idPart[..24];
        var title = string.IsNullOrWhiteSpace(wallpaper.Title) ? wallpaper.ProviderId : Sanitize(wallpaper.Title, wallpaper.ProviderId);
        if (title.Length > 48) title = title[..48].TrimEnd();
        var stem = wallpaper.ProviderId == Wallpaper.LocalProviderId
            ? title
            : $"{wallpaper.ProviderId}-{idPart}" + (string.IsNullOrWhiteSpace(wallpaper.Title) ? string.Empty : $" {title}");
        stem = Sanitize(stem);

        var candidate = Path.Combine(directory, stem + extension);
        var counter = 2;
        while (exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem} ({counter}){extension}");
            counter++;
        }
        return candidate;
    }
}
