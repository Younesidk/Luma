using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Luma.Infrastructure.Platform;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Services;

/// <summary>Download-if-needed, apply through the platform adapter and record the result in the history.</summary>
public sealed class WallpaperApplyService(
    IWallpaperApplier applier,
    IDownloadService downloads,
    IDownloadLibrary library,
    IHistoryService history,
    ILumaPaths paths,
    ILogger<WallpaperApplyService> logger,
    IImageTranscoder? transcoder = null) : IWallpaperApplyService
{
    public WallpaperApplierCapabilities Capabilities => applier.Capabilities;

    public async Task<ApplyResult> ApplyAsync(Wallpaper wallpaper, WallpaperFitMode fitMode, IProgress<double>? downloadProgress = null, CancellationToken cancellationToken = default)
    {
        if (!applier.Capabilities.IsSupported)
            return ApplyResult.Fail(ApplyFailureReason.UnsupportedPlatform, applier.Capabilities.Notes ?? "This platform can't change the wallpaper.");

        var path = !string.IsNullOrWhiteSpace(wallpaper.LocalFilePath) && File.Exists(wallpaper.LocalFilePath)
            ? wallpaper.LocalFilePath
            : library.GetLocalPath(wallpaper.Key);

        if (path is null)
        {
            var job = downloads.Enqueue(wallpaper);
            void OnChanged(object? s, EventArgs e) => downloadProgress?.Report(job.Progress);
            job.Changed += OnChanged;
            try
            {
                using var registration = cancellationToken.Register(job.Cancel);
                var stored = await job.Completion.WaitAsync(cancellationToken);
                if (stored is null)
                {
                    return job.Status == DownloadStatus.Cancelled
                        ? ApplyResult.Fail(ApplyFailureReason.Unknown, "The download was cancelled.")
                        : ApplyResult.Fail(ApplyFailureReason.Unknown, job.ErrorMessage ?? "The wallpaper couldn't be downloaded.");
                }
                path = stored.FilePath;
            }
            finally
            {
                job.Changed -= OnChanged;
            }
        }

        return await ApplyFileAsync(path, wallpaper, fitMode, cancellationToken);
    }

    public async Task<ApplyResult> ApplyFileAsync(string filePath, Wallpaper? wallpaper, WallpaperFitMode fitMode, CancellationToken cancellationToken = default)
    {
        if (WallpaperPathValidator.Validate(filePath) is { } invalid) return invalid;

        var effectivePath = filePath;
        if (applier.Capabilities.IsSupported && OperatingSystem.IsWindows() && !WallpaperPathValidator.IsNativelySupported(filePath))
        {
            if (transcoder is null)
                return ApplyResult.Fail(ApplyFailureReason.InvalidPath, "Windows can only use JPEG, PNG or BMP files as wallpaper.");

            var converted = Path.Combine(paths.DataRoot, "Cache", "Applied", Path.GetFileNameWithoutExtension(filePath) + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(converted)!);
            if (!await transcoder.ConvertToPngAsync(filePath, converted, cancellationToken))
                return ApplyResult.Fail(ApplyFailureReason.InvalidPath, "This image format couldn't be converted for use as a wallpaper.");
            effectivePath = converted;
        }

        var result = await applier.ApplyAsync(effectivePath, fitMode, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("Applying wallpaper failed: {Reason} - {Message}", result.Reason, result.Message);
            return result;
        }

        if (wallpaper is not null)
        {
            try
            {
                await history.RecordAsync(wallpaper, filePath, fitMode, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The wallpaper *was* applied; failing to record history must not turn that into an error.
                logger.LogError(ex, "Wallpaper applied but the history entry could not be saved");
            }
        }
        return result;
    }
}
