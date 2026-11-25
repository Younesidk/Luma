using Luma.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;

namespace Luma.App.Services;

/// <summary>Converts formats Windows cannot use as a wallpaper (WebP, GIF) into PNG using the Windows Imaging Component.</summary>
public sealed class ImageTranscoder(ILogger<ImageTranscoder> logger) : IImageTranscoder
{
    public Task<bool> ConvertToPngAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            try
            {
                SoftwareBitmap bitmap;
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using var inputStream = input.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(inputStream);
                    // The first frame of an animated GIF/WebP; EXIF orientation baked in so the PNG looks like the original.
                    bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                }

                using (bitmap)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    using var outputStream = output.AsRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                }
                return File.Exists(destinationPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not convert {Source} to PNG", sourcePath);
                TryDelete(destinationPath);
                return false;
            }
        }, cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a half-written file is harmless; the caller treats the conversion as failed
        }
    }
}
