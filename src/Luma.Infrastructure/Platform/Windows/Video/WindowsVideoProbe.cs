using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Infrastructure.Platform.Windows.Native;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>
/// Opens a video with the same Media Engine used for playback, so a successful probe proves the wallpaper can play it.
/// Reads size, duration and audio presence, and grabs a gallery thumbnail and a full-size still frame.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVideoProbe(ILogger<WindowsVideoProbe> logger) : IVideoProbe
{
    private const int ThumbnailWidth = 480;
    private const int PosterMaxWidth = 2560;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    private VideoHostCapabilities? _capabilities;

    public VideoHostCapabilities Capabilities => _capabilities ??= WindowsVideoSupport.Check();

    public Task<VideoProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsSupported) throw new VideoNotPlayableException(Capabilities.Notes ?? "Video playback isn't available.");
        return Task.Run(() => Probe(filePath, cancellationToken), cancellationToken);
    }

    private VideoProbeResult Probe(string filePath, CancellationToken ct)
    {
        using var session = MediaEngineSession.Create();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventReceived += (e, _, _) =>
        {
            switch (e)
            {
                case MediaEngineEvent.LoadedData:
                    loaded.TrySetResult();
                    break;
                case MediaEngineEvent.Error:
                    loaded.TrySetException(new VideoNotPlayableException(MediaErrors.Describe(session.ReadError(), filePath)));
                    break;
            }
        };

        try
        {
            session.Load(filePath);
        }
        catch (COMException ex)
        {
            throw new VideoNotPlayableException(MediaErrors.Describe((4, ex.HResult), filePath), ex);
        }

        try
        {
            loaded.Task.Wait(OpenTimeout, ct);
        }
        catch (AggregateException ex) when (ex.InnerException is VideoNotPlayableException inner)
        {
            throw inner;
        }
        if (!loaded.Task.IsCompleted) throw new VideoNotPlayableException("Windows took too long to open the video. It may be damaged.");

        if (!session.HasVideoStream) throw new VideoNotPlayableException("The file has no video picture (it may be audio only).");
        var (width, height) = session.NativeSize;
        if (width <= 0 || height <= 0) throw new VideoNotPlayableException("The video's picture size couldn't be read.");

        var seconds = session.DurationSeconds;
        var duration = double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        var hasAudio = session.HasAudioStream;
        var codec = session.VideoCodec();

        // The very first frame is often black (fade-in); take one a little way in. In frame-server mode a paused seek never
        // raises SEEKED and the frame tick can be stale while it runs, so captures are retried until a frame was really written.
        var at = duration > TimeSpan.Zero ? Math.Min(duration.TotalSeconds * 0.1, 3.0) : 0;
        if (at > 0.05) session.Seek(at);

        var (thumbW, thumbH) = VideoLayout.ScaleToWidth(width, height, ThumbnailWidth);
        var (posterW, posterH) = VideoLayout.ScaleToWidth(width, height, PosterMaxWidth);
        var poster = CaptureWithRetry(session, posterW, posterH, ct);
        if (poster is null)
        {
            // Some decoders only hand out a frame once playback has started: play (muted) for an instant.
            session.SetPlaying(true);
            poster = CaptureWithRetry(session, posterW, posterH, ct);
            session.SetPlaying(false);
        }
        if (poster is null) throw new VideoNotPlayableException("Windows opened the video but couldn't decode a picture from it. It may be damaged.");
        // Derived from the still frame on the CPU: a second GPU transfer of the same frame is intermittently empty.
        var thumbnail = PngEncoder.DownscaleBgra(poster, posterW, posterH, Math.Min(thumbW, posterW), Math.Min(thumbH, posterH));
        (thumbW, thumbH) = (Math.Min(thumbW, posterW), Math.Min(thumbH, posterH));

        logger.LogInformation("Probed {File}: {Codec} {Width}x{Height}, {Duration}, audio {Audio}, hardware device {Hardware}",
            Path.GetFileName(filePath), codec, width, height, duration, hasAudio, session.IsHardwareDevice);
        return new VideoProbeResult(width, height, duration, hasAudio,
            PngEncoder.EncodeBgra(thumbnail, thumbW, thumbH, thumbW * 4),
            PngEncoder.EncodeBgra(poster, posterW, posterH, posterW * 4, System.IO.Compression.CompressionLevel.Fastest), codec);
    }

    private static byte[]? CaptureWithRetry(MediaEngineSession session, int width, int height, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            session.TryGetNewFrame();
            // The engine writes opaque pixels; alpha 0 means the transfer produced nothing (a seek was still running).
            if (session.ReadyState >= 2 && session.CaptureFrame(width, height) is { Length: > 3 } frame && frame[3] != 0) return frame;
            Thread.Sleep(20);
        }
        return null;
    }
}

/// <summary>Turns Media Engine errors into sentences a user can act on.</summary>
[SupportedOSPlatform("windows")]
internal static class MediaErrors
{
    private const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);
    private const int MF_E_UNSUPPORTED_BYTESTREAM_TYPE = unchecked((int)0xC00D36C4);
    private const int MF_E_INVALID_FILE_FORMAT = unchecked((int)0xC00D36C3);
    private const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);

    public static string Describe((int Code, int Extended) error, string filePath)
    {
        var isWebM = string.Equals(Path.GetExtension(filePath), ".webm", StringComparison.OrdinalIgnoreCase);
        var codecHint = isWebM
            ? "WebM videos need Microsoft's free \"VP9 Video Extensions\" (or \"AV1 Video Extension\") from the Microsoft Store. Converting the video to MP4 (H.264) also works."
            : "The video probably uses a codec such as HEVC (H.265) or AV1 that needs an extension from the Microsoft Store. MP4 videos encoded with H.264 always work.";

        return error switch
        {
            (_, MF_E_TOPO_CODEC_NOT_FOUND) or (_, MF_E_INVALIDMEDIATYPE) => "Windows doesn't have a decoder for this video. " + codecHint,
            (_, MF_E_UNSUPPORTED_BYTESTREAM_TYPE) or (_, MF_E_INVALID_FILE_FORMAT) => "The file isn't a video Windows can open. It may be damaged or not really an MP4, MOV or WebM file.",
            (3, _) => "The video data is damaged or uses features Windows can't decode.",
            (4, _) => "Windows can't play this video on this PC. " + codecHint,
            (5, _) => "The video is copy-protected.",
            _ => $"Windows couldn't play the video (error 0x{error.Extended:X8})."
        };
    }
}

/// <summary>Whether this PC can play video wallpapers at all.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsVideoSupport
{
    public static VideoHostCapabilities Check()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return VideoHostCapabilities.Unsupported("Live video wallpapers need Windows 10 (version 1809) or newer.");
        if (MediaEngineSession.EnsureMediaFoundation() is { } problem) return VideoHostCapabilities.Unsupported(problem);
        try
        {
            using var session = MediaEngineSession.Create();
            return new VideoHostCapabilities(true, session.IsHardwareDevice
                ? "Videos are decoded by your graphics card where the codec allows."
                : "No graphics acceleration is available, so videos are decoded on the CPU.");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return VideoHostCapabilities.Unsupported($"Windows' video playback components couldn't be started ({ex.Message}).");
        }
    }
}
