using System.Text;
using Luma.Core.Enums;

namespace Luma.Core.Logic;

/// <summary>
/// Cheap checks done before a video is copied or handed to the playback engine. The container is recognised from the
/// file's own header, never from its extension; whether the codec inside can be decoded is decided later by opening the
/// file with the real player.
/// </summary>
public static class VideoFileValidator
{
    /// <summary>Larger files are refused: they waste disk space in the managed folder and are rarely sensible loops.</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    public static readonly IReadOnlyList<string> SupportedExtensions = [".mp4", ".m4v", ".mov", ".webm"];

    /// <summary>File-picker patterns matching <see cref="SupportedExtensions"/>.</summary>
    public static IReadOnlyList<string> PickerPatterns => SupportedExtensions.Select(e => "*" + e).ToList();

    private const int HeaderBytes = 64;

    // Atoms a QuickTime file may legitimately start with when it has no 'ftyp' box (old .mov files).
    private static readonly string[] LegacyQuickTimeAtoms = ["moov", "mdat", "wide", "free", "skip", "pnot"];

    public static bool HasSupportedExtension(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The extension the managed copy is stored with, so the player's file-type detection always agrees with the content.</summary>
    public static string CanonicalExtension(VideoContainer container) => container switch
    {
        VideoContainer.Mp4 => ".mp4",
        VideoContainer.QuickTime => ".mov",
        VideoContainer.WebM => ".webm",
        _ => throw new ArgumentOutOfRangeException(nameof(container))
    };

    public static string MimeType(VideoContainer container) => container switch
    {
        VideoContainer.Mp4 => "video/mp4",
        VideoContainer.QuickTime => "video/quicktime",
        VideoContainer.WebM => "video/webm",
        _ => "application/octet-stream"
    };

    /// <summary>Recognises the container from the first bytes. Returns null for anything else.</summary>
    public static VideoContainer? DetectContainer(ReadOnlySpan<byte> header, string? extension = null)
    {
        if (header.Length >= 12 && Ascii(header.Slice(4, 4)) == "ftyp")
        {
            var brand = Ascii(header.Slice(8, 4));
            return brand == "qt  " ? VideoContainer.QuickTime : VideoContainer.Mp4;
        }

        if (header.Length >= 8 && string.Equals(extension, ".mov", StringComparison.OrdinalIgnoreCase)
                               && LegacyQuickTimeAtoms.Contains(Ascii(header.Slice(4, 4))))
        {
            return VideoContainer.QuickTime;
        }

        // EBML magic followed by a DocType of "webm". Generic Matroska (.mkv) is deliberately not accepted.
        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            return header.IndexOf("webm"u8) > 0 ? VideoContainer.WebM : null;
        }
        return null;
    }

    /// <summary>Validates a file the user picked. Returns the container, or throws <see cref="Exceptions.VideoNotPlayableException"/> with a friendly reason.</summary>
    public static VideoContainer Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new Exceptions.VideoNotPlayableException("That file path isn't valid.");

        var name = Path.GetFileName(path);
        if (!File.Exists(path))
            throw new Exceptions.VideoNotPlayableException($"\"{name}\" no longer exists. It may have been moved or deleted.");

        if (!HasSupportedExtension(path))
            throw new Exceptions.VideoNotPlayableException($"\"{name}\" isn't a supported video. Luma plays MP4, M4V, MOV and WebM files.");

        long length;
        Span<byte> header = stackalloc byte[HeaderBytes];
        int read;
        try
        {
            length = new FileInfo(path).Length;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = stream.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Exceptions.VideoNotPlayableException($"\"{name}\" couldn't be read. It may be in use or protected.", ex);
        }

        if (length == 0) throw new Exceptions.VideoNotPlayableException($"\"{name}\" is empty.");
        if (length > MaxFileBytes)
            throw new Exceptions.VideoNotPlayableException($"\"{name}\" is larger than {MaxFileBytes / (1024 * 1024 * 1024)} GB. Use a shorter or more compressed video.");

        return DetectContainer(header[..read], Path.GetExtension(path))
               ?? throw new Exceptions.VideoNotPlayableException($"\"{name}\" doesn't look like a real MP4, MOV or WebM video. It may be damaged or have the wrong extension.");
    }

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);
}
