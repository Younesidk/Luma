using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;

namespace Luma.Tests.Fakes;

/// <summary>Stands in for the Media Foundation probe. Mocked: says nothing about real decoding (see WindowsVideoTests for that).</summary>
public sealed class FakeVideoProbe : IVideoProbe
{
    public VideoHostCapabilities Capabilities { get; set; } = new(true, null);

    /// <summary>When set, every probe fails with this user-facing message.</summary>
    public string? FailWith { get; set; }

    public int Calls { get; private set; }

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>The size each probed file reports (default 1920×1080).</summary>
    public Func<string, (int Width, int Height)> SizeOf { get; set; } = _ => (1920, 1080);

    public async Task<VideoProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWith is not null) throw new VideoNotPlayableException(FailWith);
        var pixels = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
        var png = PngEncoder.EncodeBgra(pixels, 4, 4, 16);
        var (width, height) = SizeOf(filePath);
        return new VideoProbeResult(width, height, TimeSpan.FromSeconds(12), HasAudio: false, png, png);
    }
}

/// <summary>Writes a small marker file instead of re-encoding; records what it was asked to do.</summary>
public sealed class FakeVideoOptimizer : IVideoOptimizer
{
    public const string Marker = "SHRUNK ";

    public bool IsSupported { get; set; } = true;
    public string? FailWith { get; set; }
    public List<(string Input, string Output, int Width, int Height)> Calls { get; } = [];

    public async Task ShrinkAsync(string inputPath, string outputPath, int width, int height, TimeSpan duration, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((inputPath, outputPath, width, height));
        if (FailWith is not null) throw new VideoNotPlayableException(FailWith);
        await File.WriteAllTextAsync(outputPath, $"{Marker}{width}x{height}", cancellationToken);
        progress?.Report(0.5);
        progress?.Report(1);
    }

    /// <summary>Probe sizes for files written by this fake: "SHRUNK WxH" files report W×H, everything else <paramref name="original"/>.</summary>
    public static Func<string, (int Width, int Height)> Sizes((int Width, int Height) original) => path =>
    {
        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
        if (!text.StartsWith(Marker, StringComparison.Ordinal)) return original;
        var parts = text[Marker.Length..].Split('x');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    };
}

/// <summary>Records what the service asked the desktop host to do and lets tests raise host events. Mocked: no windows exist.</summary>
public sealed class FakeDesktopVideoHost : IDesktopVideoHost
{
    private DesktopConditions _conditions = DesktopConditions.Normal;

    public VideoHostCapabilities Capabilities { get; set; } = new(true, null);

    public List<DisplayInfo> Displays { get; } =
    [
        new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true),
        new(@"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), false)
    ];

    public List<(VideoPresentation Presentation, bool StartPaused)> Shows { get; } = [];
    public List<bool> PauseCalls { get; } = [];
    public int HideCalls { get; private set; }
    public VideoPresentation? Current { get; private set; }
    public bool? Paused { get; private set; }

    /// <summary>Fail the next N ShowAsync calls.</summary>
    public int FailNextShows { get; set; }
    public bool FailuresRecoverable { get; set; } = true;

    public DesktopConditions Conditions => _conditions;

    public event EventHandler<DesktopConditions>? ConditionsChanged;
    public event EventHandler<VideoHostFailure>? Failed;
    public event EventHandler? DisplaysChanged;

    public IReadOnlyList<DisplayInfo> GetDisplays() => Displays.ToList();

    /// <summary>Simulates another Luma process owning the desktop video.</summary>
    public bool Busy { get; set; }

    public Task ShowAsync(VideoPresentation presentation, bool startPaused, CancellationToken cancellationToken = default)
    {
        if (Busy) throw new VideoHostBusyException("Another copy of Luma is already showing a live wallpaper. Close it first.");
        if (FailNextShows > 0)
        {
            FailNextShows--;
            Current = null;
            throw new VideoHostException("Simulated host failure.", FailuresRecoverable);
        }
        Shows.Add((presentation, startPaused));
        Current = presentation;
        Paused = startPaused;
        return Task.CompletedTask;
    }

    public void SetPaused(bool paused)
    {
        PauseCalls.Add(paused);
        Paused = paused;
    }

    public Task HideAsync()
    {
        HideCalls++;
        Current = null;
        Paused = null;
        return Task.CompletedTask;
    }

    public void RaiseConditions(DesktopConditions conditions)
    {
        _conditions = conditions;
        ConditionsChanged?.Invoke(this, conditions);
    }

    public void RaiseFailure(string message = "Simulated playback failure.", bool recoverable = true)
    {
        Current = null;
        Failed?.Invoke(this, new VideoHostFailure(message, recoverable));
    }

    public void RaiseDisplaysChanged() => DisplaysChanged?.Invoke(this, EventArgs.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records static wallpaper applications (the video's still frame or a picture). Mocked: the system is untouched.</summary>
public sealed class RecordingApplier : IWallpaperApplier
{
    public List<(string Path, WallpaperFitMode Fit)> Applied { get; } = [];

    public WallpaperApplierCapabilities Capabilities { get; } = new(true, "Test", [WallpaperFitMode.Fill, WallpaperFitMode.Fit, WallpaperFitMode.Stretch]);

    public Task<ApplyResult> ApplyAsync(string filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default)
    {
        Applied.Add((filePath, fitMode));
        return Task.FromResult(ApplyResult.Ok());
    }
}

public static class VideoSamples
{
    /// <summary>Bytes that pass the container sniffing (an 'ftyp' box). Distinct <paramref name="seed"/>s give distinct hashes.</summary>
    public static byte[] Mp4Bytes(int seed = 1, int size = 4096)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }.CopyTo(bytes, 0);
        return bytes;
    }

    public static byte[] WebMBytes(int seed = 1)
    {
        var bytes = new byte[2048];
        new Random(seed).NextBytes(bytes);
        new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0x82, 0x84, (byte)'w', (byte)'e', (byte)'b', (byte)'m' }.CopyTo(bytes, 0);
        return bytes;
    }

    public static string Write(string directory, string name, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
