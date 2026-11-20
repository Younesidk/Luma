using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Models;

namespace Luma.Infrastructure.Videos;

/// <summary>Used where no live-wallpaper implementation exists (Linux, macOS). Reports that honestly instead of pretending.</summary>
public sealed class UnsupportedVideoPlatform(string platformName) : IDesktopVideoHost, IVideoProbe, IVideoPreviewFactory
{
    public VideoHostCapabilities Capabilities { get; } = VideoHostCapabilities.Unsupported(
        $"Live video wallpapers need Windows 10 or 11. They aren't available on {platformName} yet; static wallpapers work as usual.");

    public DesktopConditions Conditions => DesktopConditions.Normal;

    public bool IsSupported => false;

    public event EventHandler<DesktopConditions>? ConditionsChanged { add { } remove { } }
    public event EventHandler<VideoHostFailure>? Failed { add { } remove { } }
    public event EventHandler? DisplaysChanged { add { } remove { } }

    public IReadOnlyList<DisplayInfo> GetDisplays() => [];

    public Task ShowAsync(VideoPresentation presentation, bool startPaused, CancellationToken cancellationToken = default) =>
        throw new VideoHostException(Capabilities.Notes!, recoverable: false);

    public void SetPaused(bool paused)
    {
    }

    public Task HideAsync() => Task.CompletedTask;

    public Task<VideoProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default) =>
        throw new VideoNotPlayableException(Capabilities.Notes!);

    public IVideoPreviewSurface Create(nint parentWindow, string filePath) => throw new PlatformNotSupportedException(Capabilities.Notes);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
