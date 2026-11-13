namespace Luma.Core.Interfaces;

/// <summary>Converts images the operating system cannot use as wallpaper (e.g. WebP) into PNG. Provided by the UI layer.</summary>
public interface IImageTranscoder
{
    /// <summary>Decodes <paramref name="sourcePath"/> and writes a PNG to <paramref name="destinationPath"/>. Returns false on failure.</summary>
    Task<bool> ConvertToPngAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}
