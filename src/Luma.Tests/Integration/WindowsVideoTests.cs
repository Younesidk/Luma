using System.Diagnostics;
using System.Runtime.Versioning;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Infrastructure.Platform.Windows.Native;
using Luma.Infrastructure.Platform.Windows.Video;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luma.Tests.Integration;

/// <summary>Runs only on Windows with working Media Foundation; reported as skipped elsewhere (never as passed).</summary>
public sealed class WindowsMediaFactAttribute : FactAttribute
{
    public WindowsMediaFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Needs Windows.";
        else if (!Support.Value.IsSupported) Skip = "Media Foundation unavailable: " + Support.Value.Notes;
    }

    private static readonly Lazy<VideoHostCapabilities> Support = new(() =>
        OperatingSystem.IsWindows() ? WindowsVideoSupport.Check() : VideoHostCapabilities.Unsupported("not Windows"));
}

/// <summary>
/// Puts real windows on the real desktop. Opt-in (set LUMA_DESKTOP_TESTS=1) because it changes what is visible on the
/// machine running the tests, and CI agents may have no interactive Explorer desktop.
/// </summary>
public sealed class WindowsDesktopFactAttribute : FactAttribute
{
    public WindowsDesktopFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Needs Windows.";
        else if (Environment.GetEnvironmentVariable("LUMA_DESKTOP_TESTS") != "1") Skip = "Set LUMA_DESKTOP_TESTS=1 to run tests that draw on the real desktop.";
    }
}

/// <summary>
/// Real Media Foundation decoding, Direct3D 11 presentation and (opt-in) the real Windows desktop. Nothing is mocked.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("Windows desktop")] // never run in parallel with each other: one desktop, one host per process
public class WindowsVideoTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Videos", name);

    private static readonly WindowsVideoProbe Probe = new(NullLogger<WindowsVideoProbe>.Instance);

    // ----- probe -----

    [WindowsMediaFact]
    public async Task Probe_reads_h264_mp4_facts_and_real_frames()
    {
        var result = await Probe.ProbeAsync(Fixture("h264.mp4"));

        Assert.Equal(160, result.Width);
        Assert.Equal(90, result.Height);
        Assert.InRange(result.Duration.TotalSeconds, 0.9, 1.1);
        Assert.False(result.HasAudio);
        Assert.Equal(new WallpaperResolution(160, 90), ImageInfoReader.TryRead(new MemoryStream(result.PosterPng))!.Resolution);
        Assert.Equal(new WallpaperResolution(160, 90), ImageInfoReader.TryRead(new MemoryStream(result.ThumbnailPng))!.Resolution);
        // A real colour-bar frame compresses far worse than a blank one; a black/empty capture would be a few hundred bytes.
        Assert.True(result.ThumbnailPng.Length > 3000, $"thumbnail looks blank ({result.ThumbnailPng.Length} bytes)");
    }

    [WindowsMediaFact]
    public async Task Probe_accepts_quicktime_and_detects_audio()
    {
        Assert.Equal(160, (await Probe.ProbeAsync(Fixture("h264.mov"))).Width);
        Assert.True((await Probe.ProbeAsync(Fixture("with-audio.mp4"))).HasAudio);
    }

    [WindowsMediaFact]
    public async Task Probe_handles_webm_and_hevc_honestly_whether_or_not_the_codec_extension_is_installed()
    {
        // These codecs come from optional Microsoft Store extensions: either they decode, or the message says what to install.
        await AssertPlaysOrExplains(Fixture("vp9.webm"), "VP9 Video Extensions");
        await AssertPlaysOrExplains(Fixture("hevc.mp4"), "HEVC");

        static async Task AssertPlaysOrExplains(string path, string hint)
        {
            try
            {
                var result = await Probe.ProbeAsync(path);
                Assert.Equal(160, result.Width);
            }
            catch (VideoNotPlayableException ex)
            {
                Assert.Contains(hint, ex.Message);
            }
        }
    }

    [WindowsMediaFact]
    public async Task Probe_rejects_damaged_and_fake_files_with_a_reason()
    {
        var corrupt = await Assert.ThrowsAsync<VideoNotPlayableException>(() => Probe.ProbeAsync(Fixture("corrupt.mp4")));
        Assert.False(string.IsNullOrWhiteSpace(corrupt.Message));
        var fake = await Assert.ThrowsAsync<VideoNotPlayableException>(() => Probe.ProbeAsync(Fixture("not-a-video.mp4")));
        Assert.Contains("isn't a video", fake.Message);
    }

    // ----- renderer (real decoder + real swap chain in an off-desktop window) -----

    [WindowsMediaFact]
    public async Task Renderer_loops_a_one_second_clip_and_pauses_without_presenting()
    {
        using var window = HiddenWindow.Create(320, 180);
        using var renderer = new VideoRenderer(NullLogger.Instance, "test renderer");
        await renderer.LoadAsync(Fixture("h264.mp4")).WaitAsync(TimeSpan.FromSeconds(15));
        await renderer.SetTargetsAsync([new RenderTarget(window.Handle, 320, 180, WallpaperFitMode.Fill)]);
        renderer.SetPlaying(true);
        await renderer.WaitForFrameAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // 3 s of a 1 s / 30 fps clip: well beyond 30 frames proves it loops instead of stopping at the end.
        await Task.Delay(3000);
        var playing = renderer.FramesPresented;
        Assert.True(playing > 60, $"only {playing} frames in ~3 s: not looping?");

        renderer.SetPlaying(false);
        await Task.Delay(300);
        var pausedAt = renderer.FramesPresented;
        await Task.Delay(1000);
        Assert.InRange(renderer.FramesPresented - pausedAt, 0, 1); // no decoding/presenting while paused

        renderer.SetPlaying(true);
        await Task.Delay(1000);
        Assert.True(renderer.FramesPresented - pausedAt > 15, "did not resume");
    }

    [WindowsMediaFact]
    public async Task Renderer_reports_an_unplayable_file_as_a_non_recoverable_failure()
    {
        using var renderer = new VideoRenderer(NullLogger.Instance, "test renderer");
        var ex = await Assert.ThrowsAsync<VideoHostException>(() => renderer.LoadAsync(Fixture("not-a-video.mp4")).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(ex.Recoverable);
    }

    [WindowsMediaFact]
    public async Task Creating_and_disposing_players_repeatedly_does_not_leak()
    {
        using var window = HiddenWindow.Create(320, 180);
        async Task Cycle()
        {
            using var renderer = new VideoRenderer(NullLogger.Instance, "leak test");
            await renderer.LoadAsync(Fixture("h264.mp4")).WaitAsync(TimeSpan.FromSeconds(15));
            await renderer.SetTargetsAsync([new RenderTarget(window.Handle, 320, 180, WallpaperFitMode.Fit)]);
            renderer.SetPlaying(true);
            await renderer.WaitForFrameAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Warm up: first use loads Media Foundation, codecs and the GPU driver.
        for (var i = 0; i < 3; i++) await Cycle();
        var process = Process.GetCurrentProcess();
        Settle(process);
        var handlesBefore = process.HandleCount;
        var bytesBefore = process.PrivateMemorySize64;
        var threadsBefore = process.Threads.Count;

        for (var i = 0; i < 20; i++) await Cycle();

        Settle(process);
        var handleGrowth = process.HandleCount - handlesBefore;
        var memoryGrowth = (process.PrivateMemorySize64 - bytesBefore) / (1024.0 * 1024);
        var threadGrowth = process.Threads.Count - threadsBefore;
        Console.WriteLine($"20 player cycles: handles {handleGrowth:+0;-0}, private memory {memoryGrowth:+0.0;-0.0} MB, threads {threadGrowth:+0;-0}");
        Assert.True(handleGrowth < 100, $"handles grew by {handleGrowth}");
        Assert.True(memoryGrowth < 40, $"private memory grew by {memoryGrowth:0.0} MB");
        Assert.True(threadGrowth < 10, $"threads grew by {threadGrowth}");

        static void Settle(Process p)
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Thread.Sleep(500);
            p.Refresh();
        }
    }

    // ----- the real desktop (opt-in) -----

    [WindowsDesktopFact]
    public async Task Desktop_host_places_one_window_per_display_directly_below_the_icons_and_cleans_up()
    {
        await using var host = new WindowsDesktopVideoHost(NullLogger<WindowsDesktopVideoHost>.Instance);
        var displays = host.GetDisplays();
        Assert.NotEmpty(displays);
        var names = displays.Select(d => d.DeviceName).ToList();

        await host.ShowAsync(new VideoPresentation(Fixture("h264.mp4"), names, WallpaperFitMode.Fill), startPaused: false);
        var outputs = DesktopWindows();
        Assert.Equal(displays.Count, outputs.Count);
        Assert.All(outputs, o => Assert.True(User32.IsWindowVisible(o)));
        AssertBelowIcons(outputs);

        var frames = host.FramesPresented;
        await Task.Delay(1500);
        Assert.True(host.FramesPresented - frames > 20, "frames are not being presented on the desktop");

        // Showing again (same file, e.g. after a settings change) must not duplicate windows or decoders.
        await host.ShowAsync(new VideoPresentation(Fixture("h264.mp4"), names, WallpaperFitMode.Fit), startPaused: false);
        await host.ShowAsync(new VideoPresentation(Fixture("h264.mov"), names, WallpaperFitMode.Fit), startPaused: false);
        Assert.Equal(displays.Count, DesktopWindows().Count);

        await host.HideAsync();
        Assert.Empty(DesktopWindows());
    }

    [WindowsDesktopFact]
    public async Task Desktop_host_rebuilds_when_its_window_is_destroyed_by_someone_else()
    {
        await using var host = new WindowsDesktopVideoHost(NullLogger<WindowsDesktopVideoHost>.Instance);
        var names = host.GetDisplays().Select(d => d.DeviceName).ToList();
        await host.ShowAsync(new VideoPresentation(Fixture("h264.mp4"), names, WallpaperFitMode.Fill), startPaused: false);
        var before = DesktopWindows();

        // Same path Explorer takes when it recreates its desktop: our window disappears without Luma asking.
        User32.PostMessage(before[0], 0x0010 /* WM_CLOSE */, 0, 0);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        IReadOnlyList<nint> after;
        do
        {
            await Task.Delay(200);
            after = DesktopWindows();
        } while ((after.Count != before.Count || after.Intersect(before).Any()) && DateTime.UtcNow < deadline);

        Assert.Equal(before.Count, after.Count);
        Assert.DoesNotContain(before[0], after);
        AssertBelowIcons(after);
        var frames = host.FramesPresented;
        await Task.Delay(1000);
        Assert.True(host.FramesPresented > frames, "playback did not continue after the rebuild");
        await host.HideAsync();
    }

    [WindowsDesktopFact]
    public async Task The_video_stays_above_the_wallpaper_when_windows_changes_the_wallpaper()
    {
        // Regression: on Windows 11 24H2+, changing the Windows wallpaper (Luma does so itself with the video's still
        // frame) makes Explorer insert a new wallpaper window above Luma's, hiding the video behind a still image.
        await using var host = new WindowsDesktopVideoHost(NullLogger<WindowsDesktopVideoHost>.Instance);
        await host.ShowAsync(new VideoPresentation(Fixture("h264.mp4"), host.GetDisplays().Select(d => d.DeviceName).ToList(), WallpaperFitMode.Fill), startPaused: false);
        var outputs = DesktopWindows();
        AssertBelowIcons(outputs);

        // Re-apply the current wallpaper: a real wallpaper change for Explorer, nothing visibly different for the user.
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
        {
            var current = key?.GetValue("WallPaper") as string;
            if (string.IsNullOrEmpty(current) || !File.Exists(current)) return; // no picture wallpaper to re-apply
            await Task.Run(() => SystemParametersInfo(0x0014, 0, current, 0x01 | 0x02));
        }

        await Task.Delay(2500); // Explorer swaps its wallpaper windows over ~1.2 s
        AssertBelowIcons(DesktopWindows());
        var frames = host.FramesPresented;
        await Task.Delay(1000);
        Assert.True(host.FramesPresented > frames, "playback stopped");
        await host.HideAsync();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

    [WindowsDesktopFact]
    public async Task A_second_host_in_another_process_is_refused_while_one_is_active()
    {
        await using var host = new WindowsDesktopVideoHost(NullLogger<WindowsDesktopVideoHost>.Instance);
        var names = host.GetDisplays().Select(d => d.DeviceName).ToList();
        await host.ShowAsync(new VideoPresentation(Fixture("h264.mp4"), names, WallpaperFitMode.Fill), startPaused: true);

        // The guard is a named mutex; from this process it shows up as "already owned by another thread".
        var probeThread = Task.Factory.StartNew(() =>
        {
            using var mutex = new Mutex(false, @"Local\Luma.LiveWallpaper.Host");
            try { return mutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }, TaskCreationOptions.LongRunning);
        Assert.False(await probeThread);
        await host.HideAsync();
    }

    private static List<nint> DesktopWindows()
    {
        var result = new List<nint>();
        foreach (var parent in DesktopParents())
        {
            nint child = 0;
            while ((child = User32.FindWindowEx(parent, child, DesktopWindowThread.OutputClassName, null)) != 0) result.Add(child);
        }
        return result;
    }

    private static IEnumerable<nint> DesktopParents()
    {
        var progman = User32.FindWindow("Progman", null);
        if (progman != 0) yield return progman;
        nint worker = 0;
        while ((worker = User32.FindWindowEx(0, worker, "WorkerW", null)) != 0) yield return worker;
    }

    /// <summary>On the 24H2+ layout our windows must sit between SHELLDLL_DefView (icons, above) and the wallpaper WorkerW (below).</summary>
    private static void AssertBelowIcons(IReadOnlyList<nint> outputs)
    {
        var progman = User32.FindWindow("Progman", null);
        var defView = User32.FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (defView == 0) return; // classic layout: covered by being a child of the wallpaper WorkerW
        var order = new List<string>();
        for (var c = GetWindow(progman, 5 /* GW_CHILD */); c != 0; c = GetWindow(c, 2 /* GW_HWNDNEXT */))
            order.Add(outputs.Contains(c) ? "luma" : User32.ClassName(c));
        var icons = order.IndexOf("SHELLDLL_DefView");
        Assert.True(icons >= 0);
        Assert.All(Enumerable.Range(icons + 1, outputs.Count), i => Assert.Equal("luma", order[i]));
        var worker = order.IndexOf("WorkerW");
        if (worker >= 0) Assert.True(worker > icons + outputs.Count, string.Join(" > ", order));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    /// <summary>An invisible top-level window that a swap chain can target (no desktop involvement).</summary>
    private sealed class HiddenWindow : IDisposable
    {
        public nint Handle { get; private init; }

        public static HiddenWindow Create(int width, int height)
        {
            var hwnd = User32.CreateWindowEx(User32.WS_EX_TOOLWINDOW, "STATIC", "Luma test surface", User32.WS_POPUP, -32000, -32000, width, height, 0, 0, 0, 0);
            Assert.NotEqual(0, hwnd);
            return new HiddenWindow { Handle = hwnd };
        }

        public void Dispose() => User32.DestroyWindow(Handle);
    }
}
