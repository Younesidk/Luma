using Avalonia.Media.Imaging;
using Luma.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luma.App.Services;

/// <summary>Converts formats Windows cannot use as a wallpaper (WebP, GIF) into PNG using Avalonia's Skia codecs.</summary>
public sealed class ImageTranscoder(ILogger<ImageTranscoder> logger) : IImageTranscoder
{
    public Task<bool> ConvertToPngAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                using var bitmap = new Bitmap(sourcePath);
                bitmap.Save(destinationPath, new PngBitmapEncoderOptions());
                return File.Exists(destinationPath);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                logger.LogWarning(ex, "Could not convert {Source} to PNG", sourcePath);
                return false;
            }
        }, cancellationToken);
}

