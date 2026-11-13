using System.Net;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Luma.Infrastructure.Database;
using Luma.Infrastructure.Downloads;
using Luma.Infrastructure.Platform;
using Luma.Infrastructure.Services;
using Luma.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luma.Tests.Integration;

public class SettingsServiceTests
{
    [Fact]
    public async Task Settings_survive_a_restart()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using (var first = env.CreateSettings())
        {
            await first.LoadAsync();
            Assert.Equal(ThemePreference.Dark, first.Current.Theme);
            first.Update(s =>
            {
                s.Theme = ThemePreference.Light;
                s.WallhavenApiKey = "key";
                s.RotationEnabled = true;
                s.RotationIntervalMinutes = 180;
                s.DefaultFitMode = WallpaperFitMode.Span;
                s.DownloadDirectory = Path.Combine(env.Root, "elsewhere");
            });
            await first.FlushAsync();
        }

        using var second = env.CreateSettings();
        await second.LoadAsync();
        Assert.Equal(ThemePreference.Light, second.Current.Theme);
        Assert.Equal("key", second.Current.WallhavenApiKey);
        Assert.True(second.Current.RotationEnabled);
        Assert.Equal(180, second.Current.RotationIntervalMinutes);
        Assert.Equal(WallpaperFitMode.Span, second.Current.DefaultFitMode);
        Assert.Equal(Path.Combine(env.Root, "elsewhere"), second.EffectiveDownloadDirectory);
        Assert.Contains("\"Light\"", await File.ReadAllTextAsync(env.Paths.SettingsPath)); // readable enum names
    }

    [Fact]
    public async Task Changes_are_saved_automatically_after_a_short_delay()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var settings = env.CreateSettings();
        await settings.LoadAsync();
        settings.Update(s => s.Density = GalleryDensity.Compact);

        for (var i = 0; i < 40 && !File.Exists(env.Paths.SettingsPath); i++) await Task.Delay(100);
        Assert.True(File.Exists(env.Paths.SettingsPath));
    }

    [Fact]
    public async Task Corrupt_settings_fall_back_to_defaults_without_throwing()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await File.WriteAllTextAsync(env.Paths.SettingsPath, "{ this is not json");
        using var settings = env.CreateSettings();
        await settings.LoadAsync();

        Assert.Equal(new AppSettings().Theme, settings.Current.Theme);
        Assert.True(File.Exists(env.Paths.SettingsPath + ".corrupt"));
    }

    [Fact]
    public async Task Reset_restores_defaults_and_notifies()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var settings = env.CreateSettings();
        await settings.LoadAsync();
        settings.Update(s => s.OpenverseEnabled = false);
        var notified = 0;
        settings.Changed += (_, _) => notified++;

        await settings.ResetAsync();

        Assert.True(settings.Current.OpenverseEnabled);
        Assert.Equal(1, notified);
        using var reloaded = env.CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.Current.OpenverseEnabled);
    }

    [Fact]
    public async Task Default_download_directory_lives_in_the_data_root()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var settings = env.CreateSettings();
        await settings.LoadAsync();
        Assert.Equal(env.Paths.DefaultDownloadDirectory, settings.EffectiveDownloadDirectory);
        Assert.StartsWith(env.Root, settings.EffectiveDownloadDirectory);
    }
}

public class DownloadServiceTests
{
    private sealed class Handlers(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        private readonly StubHandler _handler = new(responder);
        public int RequestCount => _handler.Requests.Count;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static async Task<(DownloadService Service, DownloadLibrary Library, FakeSettings Settings, Handlers Http)> CreateAsync(
        TestEnvironment env, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var settings = new FakeSettings();
        settings.Current.DownloadDirectory = Path.Combine(env.Root, "dl");
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var http = new Handlers(responder);
        await Task.CompletedTask;
        return (new DownloadService(http, settings, library, NullLogger<DownloadService>.Instance), library, settings, http);
    }

    private static HttpResponseMessage Image(byte[] bytes, string type = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(type) } } };

    [Fact]
    public async Task Successful_download_validates_names_and_registers_the_file()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (service, library, settings, _) = await CreateAsync(env, _ => Image(Samples.Png(1920, 1080)));
        var progress = new List<double>();

        var job = service.Enqueue(Samples.Wallpaper("good1", title: "Evening / Light?"));
        job.Changed += (_, _) => progress.Add(job.Progress);
        var result = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.StartsWith(settings.Current.DownloadDirectory!, result.FilePath);
        Assert.EndsWith(".png", result.FilePath);
        Assert.DoesNotContain("?", Path.GetFileName(result.FilePath));
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(new WallpaperResolution(1920, 1080), result.Wallpaper.Resolution);
        Assert.Equal(library.GetLocalPath("wallhaven:good1"), result.FilePath);
        Assert.Empty(Directory.GetFiles(settings.Current.DownloadDirectory!, "*.luma-part")); // temp files cleaned
    }

    [Fact]
    public async Task Html_or_garbage_instead_of_an_image_is_rejected_and_cleaned_up()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (service, library, settings, _) = await CreateAsync(env, _ => Image("<html>Please log in</html>"u8.ToArray(), "image/jpeg"));

        var job = service.Enqueue(Samples.Wallpaper("bad1"));
        Assert.Null(await job.Completion.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.False(string.IsNullOrWhiteSpace(job.ErrorMessage));
        Assert.Empty(await library.QueryAsync(new LibraryQuery()));
        Assert.Empty(Directory.GetFiles(settings.Current.DownloadDirectory!));

        var (service2, _, _, _) = await CreateAsync(env, _ => Image("<html/>"u8.ToArray(), "text/html"));
        var htmlJob = service2.Enqueue(Samples.Wallpaper("bad2"));
        Assert.Null(await htmlJob.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("web page", htmlJob.ErrorMessage);
    }

    [Fact]
    public async Task Server_errors_and_missing_urls_fail_with_friendly_messages()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (service, _, _, _) = await CreateAsync(env, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var job = service.Enqueue(Samples.Wallpaper("forbidden"));
        Assert.Null(await job.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("403", job.ErrorMessage);

        var noUrl = service.Enqueue(Samples.Wallpaper("nourl") with { FullUrl = null });
        Assert.Null(await noUrl.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("downloadable", noUrl.ErrorMessage);

        var offline = service.Enqueue(Samples.Wallpaper("x") with { FullUrl = "file:///C:/Windows/win.ini" });
        Assert.Null(await offline.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Same_wallpaper_is_not_downloaded_twice_and_identical_content_is_detected()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (service, library, _, http) = await CreateAsync(env, _ => Image(Samples.Png(800, 600)));

        var first = service.Enqueue(Samples.Wallpaper("one"));
        var done = await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(done);

        var third = service.Enqueue(Samples.Wallpaper("one"));
        var existing = await third.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(done.FilePath, existing!.FilePath);
        Assert.Equal("Already in your library", third.Note);

        // Different wallpaper id but identical bytes: no second file is written.
        var copy = service.Enqueue(Samples.Wallpaper("two"));
        var dup = await copy.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(done.FilePath, dup!.FilePath);
        Assert.Contains("identical", copy.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await library.QueryAsync(new LibraryQuery()));
        Assert.Equal(2, http.RequestCount); // the second wallpaper id was fetched, the first one was served from the library
    }

    [Fact]
    public async Task Jobs_can_be_cancelled_and_leave_no_partial_files()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var gate = new TaskCompletionSource();
        var settings = new FakeSettings { };
        settings.Current.DownloadDirectory = Path.Combine(env.Root, "dl");
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var slow = new SlowFactory(gate);
        var service = new DownloadService(slow, settings, library, NullLogger<DownloadService>.Instance);

        var job = service.Enqueue(Samples.Wallpaper("slow"));
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        job.Cancel();

        Assert.Null(await job.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(DownloadStatus.Cancelled, job.Status);
        Assert.Empty(await library.QueryAsync(new LibraryQuery()));
        Assert.Empty(Directory.GetFiles(settings.Current.DownloadDirectory!));
    }

    [Fact]
    public async Task Concurrency_is_bounded_by_the_setting()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var settings = new FakeSettings();
        settings.Current.DownloadDirectory = Path.Combine(env.Root, "dl");
        settings.Current.MaxConcurrentDownloads = 2;
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var counting = new CountingFactory();
        var service = new DownloadService(counting, settings, library, NullLogger<DownloadService>.Instance);

        var jobs = Enumerable.Range(0, 6).Select(i => service.Enqueue(Samples.Wallpaper("c" + i))).ToList();
        await Task.WhenAll(jobs.Select(j => j.Completion));

        Assert.All(jobs, j => Assert.Equal(DownloadStatus.Completed, j.Status));
        Assert.True(counting.MaxObserved <= 2, $"observed {counting.MaxObserved} concurrent downloads");
        Assert.True(counting.MaxObserved >= 1);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "permission")]
    [InlineData(typeof(HttpRequestException), "internet")]
    [InlineData(typeof(DirectoryNotFoundException), "no longer exists")]
    public void Errors_are_translated_into_plain_language(Type exceptionType, string expectedFragment)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.Contains(expectedFragment, DownloadErrors.Describe(exception), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disk_full_is_recognised()
    {
        var diskFull = new IOException("disk full", unchecked((int)0x80070070));
        Assert.Contains("disk space", DownloadErrors.Describe(diskFull));
    }

    private sealed class SlowFactory(TaskCompletionSource gate) : IHttpClientFactory
    {
        public TaskCompletionSource Started { get; } = new();
        public HttpClient CreateClient(string name) => new(new Handler(this, gate), disposeHandler: false);

        private sealed class Handler(SlowFactory owner, TaskCompletionSource gate) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Started.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken); // never released: only cancellation ends it
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }

    private sealed class CountingFactory : IHttpClientFactory
    {
        private int _current;
        public int MaxObserved { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: false);

        private sealed class Handler(CountingFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var now = Interlocked.Increment(ref owner._current);
                lock (owner) owner.MaxObserved = Math.Max(owner.MaxObserved, now);
                await Task.Delay(80, cancellationToken);
                Interlocked.Decrement(ref owner._current);
                // Distinct content per request so duplicate detection does not short-circuit the test.
                var png = Samples.Png(100 + (int)(request.RequestUri!.AbsoluteUri.GetHashCode() & 0xFF), 100);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            }
        }
    }
}

public class PlatformTests
{
    private sealed class FakePlatform(string desktop = "", string session = "", bool windows = false, bool linux = true, bool mac = false) : IPlatformInfo
    {
        public bool IsWindows => windows;
        public bool IsLinux => linux;
        public bool IsMacOS => mac;
        public string Name => windows ? "Windows" : linux ? "Linux" : mac ? "macOS" : "Other";
        public string DesktopEnvironment => desktop;
        public string SessionType => session;
        public string? GetEnvironmentVariable(string name) => null;
    }

    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcessResult>? handler = null) : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(handler?.Invoke(fileName, arguments) ?? new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private static string TempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), "luma-wall-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Samples.Png());
        return path;
    }

    [Theory]
    [InlineData("GNOME", "wayland", LinuxWallpaperApplier.LinuxBackend.Gnome)]
    [InlineData("ubuntu:GNOME", "x11", LinuxWallpaperApplier.LinuxBackend.Gnome)]
    [InlineData("X-Cinnamon", "x11", LinuxWallpaperApplier.LinuxBackend.Cinnamon)]
    [InlineData("MATE", "x11", LinuxWallpaperApplier.LinuxBackend.Mate)]
    [InlineData("KDE", "wayland", LinuxWallpaperApplier.LinuxBackend.Kde)]
    [InlineData("XFCE", "x11", LinuxWallpaperApplier.LinuxBackend.Xfce)]
    [InlineData("sway", "wayland", LinuxWallpaperApplier.LinuxBackend.Sway)]
    [InlineData("i3", "x11", LinuxWallpaperApplier.LinuxBackend.Feh)]
    [InlineData("river", "wayland", LinuxWallpaperApplier.LinuxBackend.None)]
    public void Linux_backend_is_detected_from_the_environment(string desktop, string session, LinuxWallpaperApplier.LinuxBackend expected)
    {
        var applier = new LinuxWallpaperApplier(new FakePlatform(desktop.ToLowerInvariant(), session), new FakeRunner(), NullLogger<LinuxWallpaperApplier>.Instance);
        Assert.Equal(expected, applier.Backend);
        Assert.Equal(expected != LinuxWallpaperApplier.LinuxBackend.None, applier.Capabilities.IsSupported);
    }

    [Fact]
    public async Task Gnome_applies_picture_options_and_both_uris_without_a_shell()
    {
        var runner = new FakeRunner();
        var applier = new LinuxWallpaperApplier(new FakePlatform("gnome", "wayland"), runner, NullLogger<LinuxWallpaperApplier>.Instance);
        var image = TempImage();
        try
        {
            var result = await applier.ApplyAsync(image, WallpaperFitMode.Fit);

            Assert.True(result.Success);
            Assert.Equal(3, runner.Calls.Count);
            Assert.All(runner.Calls, c => Assert.Equal("gsettings", c.File));
            Assert.Equal("scaled", runner.Calls[0].Args[^1]);
            Assert.StartsWith("file:///", runner.Calls[1].Args[^1]);
            Assert.Equal("picture-uri-dark", runner.Calls[2].Args[2]);
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task Missing_tool_and_failing_tool_are_reported_honestly()
    {
        var image = TempImage();
        try
        {
            var missing = new LinuxWallpaperApplier(new FakePlatform("kde"), new FakeRunner((_, _) => new ProcessResult(127, "", "", CommandNotFound: true)), NullLogger<LinuxWallpaperApplier>.Instance);
            var r1 = await missing.ApplyAsync(image, WallpaperFitMode.Fill);
            Assert.False(r1.Success);
            Assert.Equal(ApplyFailureReason.CommandNotFound, r1.Reason);

            var failing = new LinuxWallpaperApplier(new FakePlatform("kde"), new FakeRunner((_, _) => new ProcessResult(1, "", "boom")), NullLogger<LinuxWallpaperApplier>.Instance);
            var r2 = await failing.ApplyAsync(image, WallpaperFitMode.Fill);
            Assert.False(r2.Success);
            Assert.Equal(ApplyFailureReason.OperatingSystemRejected, r2.Reason);
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task Unsupported_fit_modes_unknown_desktops_and_bad_paths_are_rejected_before_running_anything()
    {
        var runner = new FakeRunner();
        var image = TempImage();
        try
        {
            var kde = new LinuxWallpaperApplier(new FakePlatform("kde"), runner, NullLogger<LinuxWallpaperApplier>.Instance);
            Assert.Equal(ApplyFailureReason.UnsupportedFitMode, (await kde.ApplyAsync(image, WallpaperFitMode.Tile)).Reason);

            var wayland = new LinuxWallpaperApplier(new FakePlatform("river", "wayland"), runner, NullLogger<LinuxWallpaperApplier>.Instance);
            var unsupported = await wayland.ApplyAsync(image, WallpaperFitMode.Fill);
            Assert.Equal(ApplyFailureReason.UnsupportedPlatform, unsupported.Reason);
            Assert.Contains("Wayland", unsupported.Message);

            var gnome = new LinuxWallpaperApplier(new FakePlatform("gnome"), runner, NullLogger<LinuxWallpaperApplier>.Instance);
            Assert.Equal(ApplyFailureReason.FileNotFound, (await gnome.ApplyAsync(Path.Combine(Path.GetTempPath(), "nope.png"), WallpaperFitMode.Fill)).Reason);
            Assert.Equal(ApplyFailureReason.InvalidPath, (await gnome.ApplyAsync("relative/path.png", WallpaperFitMode.Fill)).Reason);
            Assert.Equal(ApplyFailureReason.InvalidPath, (await gnome.ApplyAsync(image + "\n; rm -rf /", WallpaperFitMode.Fill)).Reason);

            Assert.Empty(runner.Calls);
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task Xfce_sets_every_monitor_property()
    {
        var runner = new FakeRunner((file, args) => args.Contains("-l")
            ? new ProcessResult(0, "/backdrop/screen0/monitorA/workspace0/last-image\n/backdrop/screen0/monitorB/workspace0/last-image\n/other\n", "")
            : new ProcessResult(0, "", ""));
        var applier = new LinuxWallpaperApplier(new FakePlatform("xfce", "x11"), runner, NullLogger<LinuxWallpaperApplier>.Instance);
        var image = TempImage();
        try
        {
            Assert.True((await applier.ApplyAsync(image, WallpaperFitMode.Stretch)).Success);
            Assert.Equal(1 + 2 * 2, runner.Calls.Count);
            Assert.Contains(runner.Calls, c => c.Args.Contains("/backdrop/screen0/monitorB/workspace0/image-style") && c.Args.Contains("3"));
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task Unsupported_platform_applier_never_pretends_to_succeed()
    {
        var applier = new UnsupportedWallpaperApplier("macOS");
        Assert.False(applier.Capabilities.IsSupported);
        var result = await applier.ApplyAsync("/tmp/x.png", WallpaperFitMode.Fill);
        Assert.False(result.Success);
        Assert.Equal(ApplyFailureReason.UnsupportedPlatform, result.Reason);
    }

    [Fact]
    public void Windows_applier_declares_every_fit_mode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var applier = new WindowsWallpaperApplier(NullLogger<WindowsWallpaperApplier>.Instance);
        Assert.Equal(6, applier.Capabilities.SupportedFitModes.Count);
    }

    [Fact]
    public async Task Windows_applier_rejects_unusable_files_without_touching_the_system()
    {
        if (!OperatingSystem.IsWindows()) return;
        var applier = new WindowsWallpaperApplier(NullLogger<WindowsWallpaperApplier>.Instance);
        Assert.Equal(ApplyFailureReason.FileNotFound, (await applier.ApplyAsync(Path.Combine(Path.GetTempPath(), "missing-luma.png"), WallpaperFitMode.Fill)).Reason);

        var webp = Path.Combine(Path.GetTempPath(), "luma-" + Guid.NewGuid().ToString("N") + ".webp");
        await File.WriteAllBytesAsync(webp, new byte[16]);
        try { Assert.Equal(ApplyFailureReason.InvalidPath, (await applier.ApplyAsync(webp, WallpaperFitMode.Fill)).Reason); }
        finally { File.Delete(webp); }
    }

    // ----- Apply service (fake platform adapter) -----

    private sealed class FakeApplier(bool supported = true, bool succeed = true) : IWallpaperApplier
    {
        public List<(string Path, WallpaperFitMode Mode)> Applied { get; } = [];

        public WallpaperApplierCapabilities Capabilities { get; } = new(supported, "Fake", [WallpaperFitMode.Fill], "fake note");

        public Task<ApplyResult> ApplyAsync(string filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default)
        {
            Applied.Add((filePath, fitMode));
            return Task.FromResult(succeed ? ApplyResult.Ok() : ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, "denied"));
        }
    }

    private static async Task<(WallpaperApplyService Service, FakeApplier Applier, HistoryService History, DownloadLibrary Library, TestEnvironment Env)> CreateApplyServiceAsync(
        FakeApplier applier, Func<HttpRequestMessage, HttpResponseMessage>? http = null)
    {
        var env = await TestEnvironment.CreateAsync();
        var settings = new FakeSettings();
        settings.Current.DownloadDirectory = Path.Combine(env.Root, "dl");
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var history = new HistoryService(env.Factory);
        var responder = http ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Samples.Png(640, 480)) });
        var downloads = new DownloadService(new SimpleFactory(responder), settings, library, NullLogger<DownloadService>.Instance);
        return (new WallpaperApplyService(applier, downloads, library, history, env.Paths, NullLogger<WallpaperApplyService>.Instance), applier, history, library, env);
    }

    private sealed class SimpleFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(responder), disposeHandler: false);
    }

    [Fact]
    public async Task Apply_service_downloads_applies_and_records_history()
    {
        var (service, applier, history, library, env) = await CreateApplyServiceAsync(new FakeApplier());
        await using (env)
        {
            var progress = new List<double>();
            var result = await service.ApplyAsync(Samples.Wallpaper("remote1"), WallpaperFitMode.Fill, new Progress<double>(progress.Add));

            Assert.True(result.Success);
            var call = Assert.Single(applier.Applied);
            Assert.Equal(library.GetLocalPath("wallhaven:remote1"), call.Path);
            Assert.Equal("remote1", Assert.Single(await history.GetAsync(10)).Wallpaper.ExternalId);

            // Second time uses the local copy: no download needed.
            Assert.True((await service.ApplyAsync(Samples.Wallpaper("remote1"), WallpaperFitMode.Fill)).Success);
            Assert.Equal(2, applier.Applied.Count);
        }
    }

    [Fact]
    public async Task Failed_apply_is_not_recorded_and_failure_is_surfaced()
    {
        var (service, _, history, _, env) = await CreateApplyServiceAsync(new FakeApplier(succeed: false));
        await using (env)
        {
            var result = await service.ApplyAsync(Samples.Wallpaper("r2"), WallpaperFitMode.Fill);
            Assert.False(result.Success);
            Assert.Equal("denied", result.Message);
            Assert.Empty(await history.GetAsync(10));
        }
    }

    [Fact]
    public async Task Unsupported_platform_never_downloads_or_applies()
    {
        var applier = new FakeApplier(supported: false);
        var (service, _, history, library, env) = await CreateApplyServiceAsync(applier);
        await using (env)
        {
            var result = await service.ApplyAsync(Samples.Wallpaper("r3"), WallpaperFitMode.Fill);
            Assert.Equal(ApplyFailureReason.UnsupportedPlatform, result.Reason);
            Assert.Empty(applier.Applied);
            Assert.Empty(await library.QueryAsync(new LibraryQuery()));
            Assert.Empty(await history.GetAsync(10));
        }
    }

    [Fact]
    public async Task Download_failure_during_apply_explains_why()
    {
        var (service, applier, _, _, env) = await CreateApplyServiceAsync(new FakeApplier(), _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await using (env)
        {
            var result = await service.ApplyAsync(Samples.Wallpaper("r4"), WallpaperFitMode.Fill);
            Assert.False(result.Success);
            Assert.Contains("404", result.Message);
            Assert.Empty(applier.Applied);
        }
    }

    // ----- Rotation -----

    [Fact]
    public async Task Rotation_applies_a_different_downloaded_wallpaper_each_time()
    {
        var (apply, applier, _, library, env) = await CreateApplyServiceAsync(new FakeApplier());
        await using (env)
        {
            foreach (var id in new[] { "r1", "r2", "r3" })
            {
                var path = Path.Combine(env.Root, id + ".png");
                await File.WriteAllBytesAsync(path, Samples.Png());
                await library.AddAsync(Samples.Wallpaper(id), path, id);
            }

            var settings = new FakeSettings();
            settings.Current.RotationRandom = true;
            await using var rotation = new RotationService(settings, library, apply, new StateStore(env.Factory), NullLogger<RotationService>.Instance);

            var previous = string.Empty;
            for (var i = 0; i < 12; i++)
            {
                var result = await rotation.RotateNowAsync();
                Assert.True(result.Success);
                var current = applier.Applied[^1].Path;
                Assert.NotEqual(previous, current);
                previous = current;
            }
        }
    }

    [Fact]
    public async Task Rotation_reports_when_there_is_nothing_to_rotate()
    {
        var (apply, applier, _, library, env) = await CreateApplyServiceAsync(new FakeApplier());
        await using (env)
        {
            await using var rotation = new RotationService(new FakeSettings(), library, apply, new StateStore(env.Factory), NullLogger<RotationService>.Instance);
            var result = await rotation.RotateNowAsync();
            Assert.False(result.Success);
            Assert.Empty(applier.Applied);
        }
    }

    [Fact]
    public async Task Rotation_loop_only_fires_while_enabled_and_stops_cleanly()
    {
        var (apply, applier, _, library, env) = await CreateApplyServiceAsync(new FakeApplier());
        await using (env)
        {
            foreach (var id in new[] { "a", "b" })
            {
                var path = Path.Combine(env.Root, id + ".png");
                await File.WriteAllBytesAsync(path, Samples.Png());
                await library.AddAsync(Samples.Wallpaper(id), path, id);
            }

            var settings = new FakeSettings();
            settings.Current.RotationEnabled = false;
            await using var rotation = new RotationService(settings, library, apply, new StateStore(env.Factory), NullLogger<RotationService>.Instance);
            rotation.Start();
            Assert.True(rotation.IsRunning);

            await Task.Delay(300);
            Assert.Empty(applier.Applied); // disabled: nothing happens
            Assert.Null(rotation.NextRotationAt);

            // Enabling schedules the next rotation in the future (interval is 1 hour by default).
            settings.Update(s => s.RotationEnabled = true);
            for (var i = 0; i < 50 && rotation.NextRotationAt is null; i++) await Task.Delay(50);
            Assert.NotNull(rotation.NextRotationAt);
            Assert.True(rotation.NextRotationAt > DateTimeOffset.UtcNow.AddMinutes(30));
            Assert.Empty(applier.Applied);

            await rotation.StopAsync();
            Assert.False(rotation.IsRunning);
        }
    }
}
