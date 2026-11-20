using System.Buffers.Binary;
using System.IO.Compression;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Infrastructure.Platform.Windows.Video;
using Luma.Tests.Fakes;

namespace Luma.Tests.Unit;

public class VideoFileValidatorTests
{
    [Theory]
    [InlineData("isom", VideoContainer.Mp4)]
    [InlineData("mp42", VideoContainer.Mp4)]
    [InlineData("avc1", VideoContainer.Mp4)]
    [InlineData("M4V ", VideoContainer.Mp4)]
    [InlineData("qt  ", VideoContainer.QuickTime)]
    public void Mp4_family_is_recognised_from_the_ftyp_brand(string brand, VideoContainer expected)
    {
        var header = new byte[16];
        "ftyp"u8.CopyTo(header.AsSpan(4));
        System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(header, 8);
        Assert.Equal(expected, VideoFileValidator.DetectContainer(header, ".mp4"));
    }

    [Fact]
    public void Webm_is_recognised_but_generic_matroska_is_not()
    {
        Assert.Equal(VideoContainer.WebM, VideoFileValidator.DetectContainer(VideoSamples.WebMBytes().AsSpan(0, 64), ".webm"));

        var mkv = VideoSamples.WebMBytes();
        "matr"u8.CopyTo(mkv.AsSpan(12));
        Assert.Null(VideoFileValidator.DetectContainer(mkv.AsSpan(0, 64), ".mkv"));
    }

    [Fact]
    public void Old_quicktime_files_without_ftyp_are_only_accepted_with_a_mov_extension()
    {
        var header = new byte[16];
        "moov"u8.CopyTo(header.AsSpan(4));
        Assert.Equal(VideoContainer.QuickTime, VideoFileValidator.DetectContainer(header, ".MOV"));
        Assert.Null(VideoFileValidator.DetectContainer(header, ".mp4"));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 })] // PNG
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0 })]       // GIF
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t' })]                        // truncated header
    public void Other_content_is_rejected(byte[] header) => Assert.Null(VideoFileValidator.DetectContainer(header, ".mp4"));

    [Fact]
    public void Validate_explains_every_kind_of_bad_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "luma-validate-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Contains("isn't valid", Assert.Throws<VideoNotPlayableException>(() => VideoFileValidator.Validate("relative.mp4")).Message);
            Assert.Contains("no longer exists", Assert.Throws<VideoNotPlayableException>(() => VideoFileValidator.Validate(Path.Combine(dir, "gone.mp4"))).Message);
            Assert.Contains("isn't a supported video", Assert.Throws<VideoNotPlayableException>(() => VideoFileValidator.Validate(VideoSamples.Write(dir, "clip.avi", VideoSamples.Mp4Bytes()))).Message);
            Assert.Contains("is empty", Assert.Throws<VideoNotPlayableException>(() => VideoFileValidator.Validate(VideoSamples.Write(dir, "empty.mp4", []))).Message);
            Assert.Contains("doesn't look like", Assert.Throws<VideoNotPlayableException>(() => VideoFileValidator.Validate(VideoSamples.Write(dir, "text.mp4", "hello"u8.ToArray()))).Message);

            // Content wins over the extension: a WebM named .mp4 is recognised as WebM (it is stored with the right extension).
            Assert.Equal(VideoContainer.WebM, VideoFileValidator.Validate(VideoSamples.Write(dir, "really-webm.mp4", VideoSamples.WebMBytes())));
            Assert.Equal(VideoContainer.Mp4, VideoFileValidator.Validate(VideoSamples.Write(dir, "ok.M4V", VideoSamples.Mp4Bytes())));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Extensions_mime_types_and_picker_patterns_agree()
    {
        Assert.Equal(".mp4", VideoFileValidator.CanonicalExtension(VideoContainer.Mp4));
        Assert.Equal(".mov", VideoFileValidator.CanonicalExtension(VideoContainer.QuickTime));
        Assert.Equal(".webm", VideoFileValidator.CanonicalExtension(VideoContainer.WebM));
        Assert.Equal("video/webm", VideoFileValidator.MimeType(VideoContainer.WebM));
        Assert.All(VideoFileValidator.SupportedExtensions, e => Assert.Contains("*" + e, VideoFileValidator.PickerPatterns));
        Assert.True(new Wallpaper { ProviderId = "local", ExternalId = "x", FileType = "video/mp4" }.IsVideo);
        Assert.False(new Wallpaper { ProviderId = "local", ExternalId = "x", FileType = "image/png" }.IsVideo);
    }
}

public class VideoLayoutTests
{
    [Fact]
    public void Same_aspect_uses_the_whole_frame_and_surface_in_every_mode()
    {
        foreach (var fit in VideoLayout.SupportedFitModes)
        {
            var layout = VideoLayout.Compute(1920, 1080, 3840, 2160, fit);
            Assert.Equal(NormalizedRect.Full, layout.Source);
            Assert.Equal(new PixelRect(0, 0, 3840, 2160), layout.Destination);
        }
    }

    [Fact]
    public void Fill_crops_a_wide_video_on_the_sides_without_distortion()
    {
        // 21:9 video on a 16:9 screen: keep the full height, crop left and right equally.
        var layout = VideoLayout.Compute(2560, 1080, 1920, 1080, WallpaperFitMode.Fill);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), layout.Destination);
        Assert.Equal(0f, layout.Source.Top);
        Assert.Equal(1f, layout.Source.Bottom);
        var visible = layout.Source.Right - layout.Source.Left;
        Assert.Equal(1920.0 / 1080 / (2560.0 / 1080), visible, 3);
        Assert.Equal(layout.Source.Left, 1 - layout.Source.Right, 5);

        // Scaled pixel aspect is preserved: visible source width / height == surface aspect.
        Assert.Equal(1920.0 / 1080, visible * 2560 / 1080, 3);
    }

    [Fact]
    public void Fill_crops_a_portrait_video_top_and_bottom()
    {
        var layout = VideoLayout.Compute(1080, 1920, 1920, 1080, WallpaperFitMode.Fill);
        Assert.Equal(0f, layout.Source.Left);
        Assert.Equal(1f, layout.Source.Right);
        Assert.True(layout.Source.Top > 0.3f && layout.Source.Bottom < 0.7f);
    }

    [Fact]
    public void Fit_letterboxes_and_pillarboxes_centred()
    {
        var wide = VideoLayout.Compute(2560, 1080, 1920, 1080, WallpaperFitMode.Fit);
        Assert.Equal(NormalizedRect.Full, wide.Source);
        Assert.Equal(1920, wide.Destination.Width);
        Assert.Equal(810, wide.Destination.Height);
        Assert.Equal((1080 - 810) / 2, wide.Destination.Y);

        var portrait = VideoLayout.Compute(1080, 1920, 1920, 1080, WallpaperFitMode.Fit);
        Assert.Equal(1080, portrait.Destination.Height);
        Assert.Equal(608, portrait.Destination.Width);
        Assert.Equal((1920 - 608) / 2, portrait.Destination.X);
    }

    [Fact]
    public void Stretch_fills_everything_and_image_only_modes_fall_back_to_fill()
    {
        var stretch = VideoLayout.Compute(1080, 1920, 1920, 1080, WallpaperFitMode.Stretch);
        Assert.Equal(NormalizedRect.Full, stretch.Source);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), stretch.Destination);

        Assert.Equal(WallpaperFitMode.Fill, VideoLayout.Normalize(WallpaperFitMode.Tile));
        Assert.Equal(WallpaperFitMode.Fill, VideoLayout.Normalize(WallpaperFitMode.Span));
        Assert.Equal(VideoLayout.Compute(2560, 1080, 1920, 1080, WallpaperFitMode.Fill), VideoLayout.Compute(2560, 1080, 1920, 1080, WallpaperFitMode.Center));
    }

    [Theory]
    [InlineData(0, 1080, 1920, 1080)]
    [InlineData(1920, 0, 1920, 1080)]
    [InlineData(1920, 1080, 0, 0)]
    [InlineData(-5, -5, 100, 100)]
    public void Degenerate_sizes_never_throw_or_produce_negative_rects(int vw, int vh, int sw, int sh)
    {
        var layout = VideoLayout.Compute(vw, vh, sw, sh, WallpaperFitMode.Fit);
        Assert.True(layout.Destination.Width >= 0 && layout.Destination.Height >= 0);
        Assert.Equal(NormalizedRect.Full, layout.Source);
    }

    [Theory]
    [InlineData(3840, 2160, 480, 480, 270)]
    [InlineData(1080, 1920, 480, 480, 852)]
    [InlineData(320, 180, 480, 320, 180)]   // never upscaled
    [InlineData(321, 181, 480, 320, 180)]   // even dimensions
    [InlineData(1, 1, 480, 2, 2)]
    public void Scaling_keeps_aspect_and_even_sizes(int w, int h, int max, int ew, int eh) =>
        Assert.Equal((ew, eh), VideoLayout.ScaleToWidth(w, h, max));

    private static readonly DisplayInfo Primary = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true);
    private static readonly DisplayInfo Second = new(@"\\.\DISPLAY2", new PixelRect(-2560, -200, 2560, 1440), new PixelRect(-2560, -200, 2560, 1400), false);

    [Fact]
    public void All_displays_target_covers_every_connected_display()
    {
        Assert.Equal([Primary, Second], VideoLayout.ResolveDisplays([Primary, Second], "all", out var fellBack));
        Assert.False(fellBack);
        Assert.Equal([Primary, Second], VideoLayout.ResolveDisplays([Primary, Second], null, out _));
    }

    [Fact]
    public void A_named_display_is_used_and_a_disconnected_one_falls_back_to_the_main_display()
    {
        Assert.Equal([Second], VideoLayout.ResolveDisplays([Primary, Second], @"\\.\display2", out var fellBack));
        Assert.False(fellBack);

        Assert.Equal([Primary], VideoLayout.ResolveDisplays([Second, Primary], @"\\.\DISPLAY9", out fellBack));
        Assert.True(fellBack);

        Assert.Empty(VideoLayout.ResolveDisplays([], "all", out _));
    }

    [Fact]
    public void Displays_left_of_or_above_the_main_display_map_into_the_desktop_window()
    {
        // The desktop window spans the virtual screen, whose origin is the top-left of all displays (-2560, -200).
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), VideoLayout.ToParentCoordinates(Second.Bounds, -2560, -200));
        Assert.Equal(new PixelRect(2560, 200, 1920, 1080), VideoLayout.ToParentCoordinates(Primary.Bounds, -2560, -200));
    }

    [Fact]
    public void Display_descriptions_are_readable()
    {
        Assert.Equal("Display 1  ·  1920×1080  ·  main", Primary.Describe(0));
        Assert.Equal("Display 2  ·  2560×1440", Second.Describe(1));
    }
}

public class VideoPlaybackPolicyTests
{
    private static readonly string[] Both = [@"\\.\DISPLAY1", @"\\.\DISPLAY2"];

    private static DesktopConditions Covered(params string[] displays) =>
        new() { CoveredDisplays = new HashSet<string>(displays, StringComparer.OrdinalIgnoreCase) };

    [Fact]
    public void Normal_conditions_play() =>
        Assert.Equal(PauseReasons.None, VideoPlaybackPolicy.Evaluate(DesktopConditions.Normal, false, true, true, Both));

    [Fact]
    public void Each_condition_maps_to_its_reason()
    {
        Assert.Equal(PauseReasons.User, VideoPlaybackPolicy.Evaluate(DesktopConditions.Normal, true, true, true, Both));
        Assert.Equal(PauseReasons.FullscreenApp, VideoPlaybackPolicy.Evaluate(new() { FullscreenAppRunning = true }, false, true, true, Both));
        Assert.Equal(PauseReasons.OnBattery, VideoPlaybackPolicy.Evaluate(new() { OnBattery = true }, false, true, true, Both));
        Assert.Equal(PauseReasons.SessionLocked, VideoPlaybackPolicy.Evaluate(new() { SessionLocked = true }, false, true, true, Both));
        Assert.Equal(PauseReasons.DisplaysOff, VideoPlaybackPolicy.Evaluate(new() { DisplaysOff = true }, false, true, true, Both));
        Assert.Equal(PauseReasons.Suspended, VideoPlaybackPolicy.Evaluate(new() { Suspended = true }, false, true, true, Both));
    }

    [Fact]
    public void Fullscreen_and_battery_pauses_respect_the_settings()
    {
        var conditions = new DesktopConditions { FullscreenAppRunning = true, OnBattery = true };
        Assert.Equal(PauseReasons.None, VideoPlaybackPolicy.Evaluate(conditions, false, pauseOnFullscreen: false, pauseOnBattery: false, Both));
        Assert.Equal(PauseReasons.FullscreenApp | PauseReasons.OnBattery, VideoPlaybackPolicy.Evaluate(conditions, false, true, true, Both));
    }

    [Fact]
    public void Desktop_hidden_only_when_every_shown_display_is_covered()
    {
        Assert.Equal(PauseReasons.None, VideoPlaybackPolicy.Evaluate(Covered(@"\\.\DISPLAY1"), false, true, true, Both));
        Assert.Equal(PauseReasons.DesktopHidden, VideoPlaybackPolicy.Evaluate(Covered(@"\\.\display1", @"\\.\DISPLAY2"), false, true, true, Both));
        // The video only on display 2: covering display 1 is irrelevant, covering display 2 pauses.
        Assert.Equal(PauseReasons.None, VideoPlaybackPolicy.Evaluate(Covered(@"\\.\DISPLAY1"), false, true, true, [@"\\.\DISPLAY2"]));
        Assert.Equal(PauseReasons.DesktopHidden, VideoPlaybackPolicy.Evaluate(Covered(@"\\.\DISPLAY2"), false, true, true, [@"\\.\DISPLAY2"]));
        // Hidden is independent of the user's settings (nobody can see it).
        Assert.Equal(PauseReasons.DesktopHidden, VideoPlaybackPolicy.Evaluate(Covered(Both), false, false, false, Both));
        Assert.Equal(PauseReasons.None, VideoPlaybackPolicy.Evaluate(Covered(Both), false, true, true, []));
    }

    [Fact]
    public void Descriptions_prefer_the_most_relevant_reason()
    {
        Assert.Equal("Playing", VideoPlaybackPolicy.Describe(PauseReasons.None));
        Assert.Equal("Paused", VideoPlaybackPolicy.Describe(PauseReasons.User | PauseReasons.OnBattery));
        Assert.Equal("Paused on battery power", VideoPlaybackPolicy.Describe(PauseReasons.OnBattery | PauseReasons.DesktopHidden));
        Assert.Equal("Paused while a fullscreen app is running", VideoPlaybackPolicy.Describe(PauseReasons.FullscreenApp));
        Assert.Equal("Paused while the desktop is hidden", VideoPlaybackPolicy.Describe(PauseReasons.DesktopHidden));
        Assert.Equal("Paused while Windows is locked", VideoPlaybackPolicy.Describe(PauseReasons.SessionLocked | PauseReasons.DesktopHidden));
    }
}

public class DesktopOcclusionTests
{
    private static readonly DisplayInfo Main = new("A", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true);
    private static readonly DisplayInfo AutoHideTaskbar = new("B", new PixelRect(1920, 0, 1920, 1080), new PixelRect(1920, 0, 1920, 1080), false);

    [Fact]
    public void A_borderless_window_covering_the_display_is_a_fullscreen_app()
    {
        var result = DesktopOcclusion.Evaluate([new WindowSnapshot(new PixelRect(0, 0, 1920, 1080), IsMaximized: false)], [Main, AutoHideTaskbar]);
        Assert.True(result.FullscreenAppRunning);
        Assert.Equal(["A"], result.CoveredDisplays);
    }

    [Fact]
    public void A_maximised_window_hides_the_desktop_but_is_not_fullscreen()
    {
        // Maximised windows extend a few pixels past the work area (invisible borders).
        var result = DesktopOcclusion.Evaluate([new WindowSnapshot(new PixelRect(-8, -8, 1936, 1056), IsMaximized: true)], [Main]);
        Assert.False(result.FullscreenAppRunning);
        Assert.Equal(["A"], result.CoveredDisplays);
    }

    [Fact]
    public void Maximised_window_with_an_auto_hiding_taskbar_is_still_not_fullscreen()
    {
        // Regression: the work area equals the whole display, so the maximised window covers it entirely.
        var result = DesktopOcclusion.Evaluate([new WindowSnapshot(new PixelRect(1912, -8, 1936, 1096), IsMaximized: true)], [AutoHideTaskbar]);
        Assert.False(result.FullscreenAppRunning);
        Assert.Equal(["B"], result.CoveredDisplays);
    }

    [Fact]
    public void Ordinary_windows_and_windows_on_other_displays_cover_nothing()
    {
        var result = DesktopOcclusion.Evaluate(
            [new WindowSnapshot(new PixelRect(100, 100, 800, 600), false), new WindowSnapshot(new PixelRect(1920, 0, 1920, 1080), false)],
            [Main]);
        Assert.False(result.FullscreenAppRunning);
        Assert.Empty(result.CoveredDisplays);
    }

    [Fact]
    public void The_system_fullscreen_signal_is_honoured_even_without_a_matching_window() =>
        Assert.True(DesktopOcclusion.Evaluate([], [Main], systemReportsFullscreen: true).FullscreenAppRunning);

    private static DesktopOcclusion.Result Sample(bool fullscreen, params string[] covered) =>
        new(fullscreen, new HashSet<string>(covered, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void A_one_sample_blip_never_pauses_the_video()
    {
        // Regression: a window shown for an instant during a wallpaper change paused the video for a second.
        var quiet = Sample(false);
        var blip = Sample(true, "A");
        var confirmed = DesktopOcclusion.Confirm(quiet, blip);
        Assert.False(confirmed.FullscreenAppRunning);
        Assert.Empty(confirmed.CoveredDisplays);
        Assert.Empty(DesktopOcclusion.Confirm(null, blip).CoveredDisplays); // first sample after showing
    }

    [Fact]
    public void A_condition_seen_twice_is_confirmed_and_clearing_it_is_immediate()
    {
        var game = Sample(true, "A", "B");
        var confirmed = DesktopOcclusion.Confirm(game, game);
        Assert.True(confirmed.FullscreenAppRunning);
        Assert.Equal(["A", "B"], confirmed.CoveredDisplays.Order());

        // The game moved to display B only and stopped being fullscreen: A is uncovered at once.
        var next = DesktopOcclusion.Confirm(game, Sample(false, "b"));
        Assert.False(next.FullscreenAppRunning);
        Assert.Equal(["b"], next.CoveredDisplays);
    }
}

public class VideoRecoveryPolicyTests
{
    [Fact]
    public void Retries_with_growing_delays_then_gives_up()
    {
        var policy = new VideoRecoveryPolicy();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(now));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.NextDelay(now));
        Assert.Equal(TimeSpan.FromSeconds(15), policy.NextDelay(now));
        Assert.Null(policy.NextDelay(now));
        Assert.Null(policy.NextDelay(now));
    }

    [Fact]
    public void Playing_steadily_for_a_minute_earns_a_fresh_set_of_retries()
    {
        var policy = new VideoRecoveryPolicy();
        var now = DateTimeOffset.UtcNow;
        policy.NextDelay(now);
        policy.NextDelay(now);
        policy.NextDelay(now);

        policy.OnPlaying(now);
        Assert.Null(policy.NextDelay(now + TimeSpan.FromSeconds(30))); // flapping: not stable long enough

        policy.OnPlaying(now);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(now + VideoRecoveryPolicy.StableAfter));
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var policy = new VideoRecoveryPolicy();
        for (var i = 0; i < 5; i++) policy.NextDelay(DateTimeOffset.UtcNow);
        policy.Reset();
        Assert.Equal(0, policy.Attempts);
        Assert.NotNull(policy.NextDelay(DateTimeOffset.UtcNow));
    }
}

public class PngEncoderTests
{
    [Fact]
    public void Encoded_frames_decode_back_to_the_same_pixels()
    {
        const int width = 7, height = 5, stride = width * 4 + 12; // padded rows, like a mapped GPU texture
        var bgra = new byte[stride * height];
        var random = new Random(42);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var o = y * stride + x * 4;
            bgra[o] = (byte)random.Next(256);
            bgra[o + 1] = (byte)random.Next(256);
            bgra[o + 2] = (byte)random.Next(256);
            bgra[o + 3] = 255;
        }

        var png = PngEncoder.EncodeBgra(bgra, width, height, stride);

        var info = ImageInfoReader.TryRead(new MemoryStream(png));
        Assert.NotNull(info);
        Assert.Equal(new WallpaperResolution(width, height), info!.Resolution);

        var rgb = DecodeRgb(png, width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var s = y * stride + x * 4;
            var d = (y * width + x) * 3;
            Assert.Equal(bgra[s + 2], rgb[d]);
            Assert.Equal(bgra[s + 1], rgb[d + 1]);
            Assert.Equal(bgra[s], rgb[d + 2]);
        }
    }

    [Fact]
    public void Downscaling_averages_areas_and_keeps_pixels_opaque()
    {
        // 4x2 source: left half pure blue, right half pure red -> 2x1 keeps the split exactly.
        var src = new byte[4 * 2 * 4];
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 4; x++)
        {
            var o = (y * 4 + x) * 4;
            src[o] = (byte)(x < 2 ? 255 : 0);
            src[o + 2] = (byte)(x < 2 ? 0 : 255);
        }
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, PngEncoder.DownscaleBgra(src, 4, 2, 2, 1));

        // 1x1 of everything averages to the mean (0+255)/2.
        Assert.Equal(new byte[] { 127, 0, 127, 255 }, PngEncoder.DownscaleBgra(src, 4, 2, 1, 1));

        // Same size is a copy (with alpha forced opaque).
        Assert.Equal(src.Length, PngEncoder.DownscaleBgra(src, 4, 2, 4, 2).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.DownscaleBgra(src, 4, 2, 5, 2));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.EncodeBgra(new byte[16], 0, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.EncodeBgra(new byte[16], 2, 2, 4));
        Assert.Throws<ArgumentException>(() => PngEncoder.EncodeBgra(new byte[10], 2, 2, 8));
    }

    /// <summary>Bit-by-bit CRC-32 (independent of the encoder's table implementation).</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Independent minimal decoder: verifies chunk CRCs, inflates IDAT and undoes the Sub filter.</summary>
    private static byte[] DecodeRgb(byte[] png, int width, int height)
    {
        var idat = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(crc, Crc32(png.AsSpan(offset + 4, 4 + length)));
            if (type == "IDAT") idat.Write(data);
            offset += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal(height * (1 + width * 3), bytes.Length);

        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            Assert.Equal(1, bytes[row]); // Sub filter
            for (var i = 0; i < width * 3; i++)
            {
                var left = i >= 3 ? rgb[y * width * 3 + i - 3] : 0;
                rgb[y * width * 3 + i] = (byte)(bytes[row + 1 + i] + left);
            }
        }
        return rgb;
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class DesktopLayoutTests
{
    [Fact]
    public void Windows_11_24H2_layout_goes_inside_progman_directly_below_the_icons()
    {
        var target = DesktopShell.Decide(new DesktopSnapshot(Progman: 0x10, DefViewInProgman: 0x20, WorkerWInProgman: 0x30, LegacyWorkerW: 0));
        Assert.NotNull(target);
        Assert.Equal((nint)0x10, target!.Parent);
        Assert.Equal((nint)0x20, target.InsertAfter);
        Assert.Equal("progman-24h2", target.Layout);
    }

    [Fact]
    public void Classic_layout_uses_the_wallpaper_workerw_behind_the_icon_host()
    {
        var target = DesktopShell.Decide(new DesktopSnapshot(Progman: 0x10, DefViewInProgman: 0, WorkerWInProgman: 0, LegacyWorkerW: 0x40));
        Assert.Equal((nint)0x40, target!.Parent);
        Assert.Equal((nint)0, target.InsertAfter);
        Assert.Equal("workerw-classic", target.Layout);
    }

    [Fact]
    public void Without_explorer_or_a_usable_layout_there_is_no_target()
    {
        Assert.Null(DesktopShell.Decide(new DesktopSnapshot(0, 0, 0, 0)));
        Assert.Null(DesktopShell.Decide(new DesktopSnapshot(0x10, 0, 0, 0)));
        Assert.Equal("progman", DesktopShell.Decide(new DesktopSnapshot(0x10, 0x20, 0, 0))!.Layout);
    }

    [Theory]
    [InlineData(2, "Progman", false)]                 // regression: QUNS_BUSY while the desktop has focus paused the video
    [InlineData(2, "WorkerW", false)]
    [InlineData(2, "Chrome_WidgetWin_1", false)]      // BUSY alone is never trusted; real fullscreen windows are measured
    [InlineData(3, "UnityWndClass", true)]            // exclusive-fullscreen game
    [InlineData(4, "PPTFrameClass", true)]            // presentation mode
    [InlineData(3, "Progman", false)]                 // nothing counts while the desktop is focused
    [InlineData(4, "Shell_TrayWnd", false)]
    [InlineData(5, "Notepad", false)]                 // QUNS_ACCEPTS_NOTIFICATIONS
    [InlineData(1, null, false)]                      // QUNS_NOT_PRESENT
    [InlineData(3, null, true)]
    public void Windows_fullscreen_signal_is_interpreted_without_mistaking_the_desktop_for_an_app(int state, string? foreground, bool expected) =>
        Assert.Equal(expected, DesktopShell.IsFullscreenSignal(state, foreground));

    [Theory]
    [InlineData(4, unchecked((int)0xC00D5212), ".webm", "VP9 Video Extensions")]
    [InlineData(4, unchecked((int)0xC00D5212), ".mp4", "HEVC")]
    [InlineData(4, unchecked((int)0xC00D36C4), ".mp4", "isn't a video Windows can open")]
    [InlineData(3, 0, ".mp4", "damaged")]
    [InlineData(5, 0, ".mp4", "copy-protected")]
    [InlineData(9, 0x1234, ".mp4", "0x00001234")]
    public void Media_errors_become_actionable_sentences(int code, int extended, string extension, string expected)
    {
        Assert.Contains(expected, MediaErrors.Describe((code, extended), "C:\\v\\clip" + extension));
    }
}

public class VideoSettingsTests
{
    [Fact]
    public void Video_settings_have_safe_defaults_and_bad_values_are_normalised()
    {
        var settings = new AppSettings();
        Assert.Equal("all", settings.VideoDisplayTarget);
        Assert.Equal(WallpaperFitMode.Fill, settings.VideoFitMode);
        Assert.True(settings.VideoPauseOnFullscreen);
        Assert.True(settings.VideoPauseOnBattery);

        settings.VideoDisplayTarget = "  ";
        settings.VideoFitMode = WallpaperFitMode.Tile;
        settings.Normalize();
        Assert.Equal("all", settings.VideoDisplayTarget);
        Assert.Equal(WallpaperFitMode.Fill, settings.VideoFitMode);
    }

    [Fact]
    public async Task Video_settings_survive_a_restart()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var first = env.CreateSettings();
        await first.LoadAsync();
        first.Update(s =>
        {
            s.VideoDisplayTarget = @"\\.\DISPLAY2";
            s.VideoFitMode = WallpaperFitMode.Fit;
            s.VideoPauseOnBattery = false;
        });
        await first.FlushAsync();

        var second = env.CreateSettings();
        await second.LoadAsync();
        Assert.Equal(@"\\.\DISPLAY2", second.Current.VideoDisplayTarget);
        Assert.Equal(WallpaperFitMode.Fit, second.Current.VideoFitMode);
        Assert.False(second.Current.VideoPauseOnBattery);
        Assert.True(second.Current.VideoPauseOnFullscreen);
    }
}
