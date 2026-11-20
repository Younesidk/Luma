using Luma.Core.Enums;
using Luma.Core.Models;
using Luma.Infrastructure.Database;
using Luma.Infrastructure.Downloads;
using Luma.Infrastructure.Services;
using Luma.Infrastructure.Videos;
using Luma.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Luma.Tests.Integration;

/// <summary>
/// The video wallpaper workflow end to end over a real database, real settings persistence and real files, with the
/// desktop host and the wallpaper applier mocked. These tests prove the orchestration (selection, persistence, policy,
/// recovery, switching); they do NOT prove that anything appears on a Windows desktop — see WindowsVideoTests.
/// </summary>
public sealed class VideoWallpaperServiceTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;
    private FakeSettings _settings = null!;
    private StateStore _state = null!;
    private VideoLibrary _library = null!;
    private FakeDesktopVideoHost _host = null!;
    private RecordingApplier _applier = null!;
    private FakeTimeProvider _time = null!;
    private VideoWallpaperService _service = null!;

    public async Task InitializeAsync()
    {
        _env = await TestEnvironment.CreateAsync();
        _settings = new FakeSettings();
        _state = new StateStore(_env.Factory);
        _library = new VideoLibrary(_env.Factory, new FakeVideoProbe(), _env.Paths, NullLogger<VideoLibrary>.Instance);
        _host = new FakeDesktopVideoHost();
        _applier = new RecordingApplier();
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _service = NewService();
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        await _env.DisposeAsync();
    }

    private VideoWallpaperService NewService() =>
        new(_library, _host, _applier, _settings, _state, NullLogger<VideoWallpaperService>.Instance, _time);

    private async Task<VideoWallpaper> ImportAsync(string name, int seed) =>
        (await _library.ImportAsync(VideoSamples.Write(Path.Combine(_env.Root, "src"), name, VideoSamples.Mp4Bytes(seed)))).Video;

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Applying_shows_the_video_on_every_display_persists_it_and_sets_its_still_frame()
    {
        var video = await ImportAsync("waves.mp4", 1);
        var statuses = new List<VideoWallpaperStatus>();
        _service.StatusChanged += (_, s) => statuses.Add(s);

        var result = await _service.ApplyAsync(video.Id);

        Assert.True(result.Success);
        var (presentation, startPaused) = Assert.Single(_host.Shows);
        Assert.False(startPaused);
        Assert.Equal(video.FilePath, presentation.FilePath);
        Assert.Equal([@"\\.\DISPLAY1", @"\\.\DISPLAY2"], presentation.DisplayDeviceNames);
        Assert.Equal(WallpaperFitMode.Fill, presentation.Fit);
        Assert.Equal((video.PosterPath!, WallpaperFitMode.Fill), Assert.Single(_applier.Applied));
        Assert.Equal(video.Id.ToString(), await _state.GetAsync(VideoWallpaperService.ActiveKey));
        Assert.Equal(VideoPlaybackState.Playing, _service.Status.State);
        Assert.Equal(2, _service.Status.DisplayCount);
        Assert.True(_service.IsActive);
        Assert.Equal([VideoPlaybackState.Starting, VideoPlaybackState.Playing], statuses.Select(s => s.State));
    }

    [Fact]
    public async Task A_single_display_target_is_honoured_and_a_missing_one_falls_back_with_an_explanation()
    {
        var video = await ImportAsync("a.mp4", 1);
        _settings.Current.VideoDisplayTarget = @"\\.\DISPLAY2";
        _settings.Current.VideoFitMode = WallpaperFitMode.Fit;
        await _service.ApplyAsync(video.Id);
        Assert.Equal([@"\\.\DISPLAY2"], _host.Current!.DisplayDeviceNames);
        Assert.Equal(WallpaperFitMode.Fit, _host.Current.Fit);
        Assert.Null(_service.Status.Message);

        _host.Displays.RemoveAt(1); // display 2 unplugged
        _host.RaiseDisplaysChanged();
        await WaitUntil(() => _host.Shows.Count == 2, "re-show after display change");
        Assert.Equal([@"\\.\DISPLAY1"], _host.Current!.DisplayDeviceNames);
        Assert.Contains("isn't connected", _service.Status.Message);
    }

    [Fact]
    public async Task Missing_entries_missing_files_and_unsupported_platforms_fail_without_touching_the_desktop()
    {
        Assert.False((await _service.ApplyAsync(424242)).Success);

        var video = await ImportAsync("gone.mp4", 2);
        File.Delete(video.FilePath);
        var result = await _service.ApplyAsync(video.Id);
        Assert.Equal(ApplyFailureReason.FileNotFound, result.Reason);

        _host.Capabilities = VideoHostCapabilities.Unsupported("Not here.");
        Assert.Equal(ApplyFailureReason.UnsupportedPlatform, (await _service.ApplyAsync(video.Id)).Reason);

        Assert.Empty(_host.Shows);
        Assert.Empty(_applier.Applied);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.ActiveKey));
    }

    [Fact]
    public async Task A_video_that_fails_to_start_is_reported_and_not_remembered()
    {
        var video = await ImportAsync("broken.mp4", 3);
        _host.FailNextShows = 1;

        var result = await _service.ApplyAsync(video.Id);

        Assert.False(result.Success);
        Assert.Equal(VideoPlaybackState.Failed, _service.Status.State);
        Assert.Contains("Simulated host failure", _service.Status.Message);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.ActiveKey));
        Assert.Empty(_applier.Applied);       // the still frame is only set once the video really plays
        Assert.True(_host.HideCalls >= 1);    // nothing half-shown stays on the desktop
    }

    [Fact]
    public async Task Switching_between_two_videos_replaces_the_first()
    {
        var first = await ImportAsync("first.mp4", 4);
        var second = await ImportAsync("second.mp4", 5);

        await _service.ApplyAsync(first.Id);
        await _service.ApplyAsync(second.Id);

        Assert.Equal(second.FilePath, _host.Current!.FilePath);
        Assert.Equal(second.Id, _service.Status.Video!.Id);
        Assert.Equal(second.Id.ToString(), await _state.GetAsync(VideoWallpaperService.ActiveKey));
        Assert.Equal([first.PosterPath, second.PosterPath], _applier.Applied.Select(a => a.Path));
    }

    [Fact]
    public async Task Applying_a_static_wallpaper_turns_the_video_off_and_back_again()
    {
        var video = await ImportAsync("live.mp4", 6);
        var downloads = new DownloadService(new StubHttpClientFactory(StubHandler.Status(System.Net.HttpStatusCode.NotFound), "https://example.test/"),
            _settings, new DownloadLibrary(_env.Factory, NullLogger<DownloadLibrary>.Instance), NullLogger<DownloadService>.Instance);
        var staticApply = new WallpaperApplyService(_applier, downloads, new DownloadLibrary(_env.Factory, NullLogger<DownloadLibrary>.Instance),
            new HistoryService(_env.Factory), _env.Paths, NullLogger<WallpaperApplyService>.Instance, video: _service);
        var picture = Path.Combine(_env.Root, "picture.png");
        await File.WriteAllBytesAsync(picture, Samples.Png(64, 64));

        // static -> video
        Assert.True((await staticApply.ApplyFileAsync(picture, null, WallpaperFitMode.Fill)).Success);
        Assert.Equal(0, _host.HideCalls); // nothing to turn off yet
        await _service.ApplyAsync(video.Id);
        Assert.True(_service.IsActive);

        // video -> static
        Assert.True((await staticApply.ApplyFileAsync(picture, null, WallpaperFitMode.Fill)).Success);
        Assert.False(_service.IsActive);
        Assert.Equal(VideoPlaybackState.Inactive, _service.Status.State);
        Assert.Null(_host.Current);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.ActiveKey));
        Assert.Equal(picture, _applier.Applied[^1].Path);
    }

    [Fact]
    public async Task The_active_video_and_the_users_pause_survive_a_restart()
    {
        var video = await ImportAsync("restart.mp4", 7);
        await _service.ApplyAsync(video.Id);
        await _service.SetUserPausedAsync(true);
        await _service.DisposeAsync();
        Assert.Null(_host.Current);                                           // shutdown removes the windows...
        Assert.NotNull(await _state.GetAsync(VideoWallpaperService.ActiveKey)); // ...but remembers the choice

        _host.Shows.Clear();
        _service = NewService();
        await _service.RestoreAsync();

        var (presentation, startPaused) = Assert.Single(_host.Shows);
        Assert.Equal(video.FilePath, presentation.FilePath);
        Assert.True(startPaused);
        Assert.Equal(VideoPlaybackState.Paused, _service.Status.State);
        Assert.True(_service.Status.PauseReasons.HasFlag(PauseReasons.User));
    }

    [Fact]
    public async Task Restoring_a_video_whose_file_vanished_explains_it_and_forgets_it()
    {
        var video = await ImportAsync("vanish.mp4", 8);
        await _service.ApplyAsync(video.Id);
        await _service.DisposeAsync();
        File.Delete(video.FilePath);
        _host.Shows.Clear();

        _service = NewService();
        await _service.RestoreAsync();

        Assert.Empty(_host.Shows);
        Assert.Equal(VideoPlaybackState.Failed, _service.Status.State);
        Assert.Contains("missing", _service.Status.Message);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.ActiveKey));
    }

    [Fact]
    public async Task A_second_luma_never_overwrites_the_selection_of_the_one_showing_the_video()
    {
        // Instance A applied a paused video; instance B (same database) starts while A still owns the desktop.
        var mine = await ImportAsync("a.mp4", 21);
        var other = await ImportAsync("b.mp4", 22);
        await _service.ApplyAsync(mine.Id);
        await _service.SetUserPausedAsync(true);
        await _service.DisposeAsync();
        _host.Busy = true;
        _service = NewService();

        await _service.RestoreAsync();
        Assert.Equal(VideoPlaybackState.Failed, _service.Status.State);
        Assert.Contains("Another copy of Luma", _service.Status.Message);

        var result = await _service.ApplyAsync(other.Id);
        Assert.False(result.Success);

        Assert.Equal(mine.Id.ToString(), await _state.GetAsync(VideoWallpaperService.ActiveKey));
        Assert.Equal("true", await _state.GetAsync(VideoWallpaperService.PausedKey));
        Assert.Single(_applier.Applied); // B never touched the system wallpaper
    }

    [Fact]
    public async Task Restoring_when_nothing_was_active_does_nothing()
    {
        await _service.RestoreAsync();
        Assert.Empty(_host.Shows);
        Assert.Equal(VideoPlaybackState.Inactive, _service.Status.State);
    }

    [Fact]
    public async Task Playback_failures_are_retried_with_backoff_and_recover()
    {
        var video = await ImportAsync("flaky.mp4", 9);
        await _service.ApplyAsync(video.Id);

        _host.RaiseFailure();
        await WaitUntil(() => _service.Status.State == VideoPlaybackState.Recovering, "recovering");
        Assert.Single(_host.Shows);

        await Task.Delay(30); // let the retry timer register before moving the fake clock
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => _host.Shows.Count == 2, "retry");
        await WaitUntil(() => _service.Status.State == VideoPlaybackState.Playing, "playing again");
        Assert.Equal(video.FilePath, _host.Current!.FilePath);
    }

    [Fact]
    public async Task Repeated_failures_give_up_keep_the_still_frame_and_stop_retrying()
    {
        var video = await ImportAsync("dead.mp4", 10);
        await _service.ApplyAsync(video.Id);
        _host.FailNextShows = int.MaxValue;

        var recoveries = 0;
        _service.StatusChanged += (_, s) =>
        {
            if (s.State == VideoPlaybackState.Recovering) Interlocked.Increment(ref recoveries);
        };

        _host.RaiseFailure();
        var expected = 0;
        foreach (var delay in new[] { 1, 5, 15 })
        {
            expected++;
            await WaitUntil(() => Volatile.Read(ref recoveries) == expected, $"recovery #{expected}");
            await Task.Delay(30); // let the retry timer register before moving the fake clock
            _time.Advance(TimeSpan.FromSeconds(delay));
        }

        await WaitUntil(() => _service.Status.State == VideoPlaybackState.Failed, "failed");
        Assert.Contains("stopped playing", _service.Status.Message);
        Assert.False(_service.IsActive);
        Assert.Null(_host.Current);
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.Equal(VideoPlaybackState.Failed, _service.Status.State); // no endless loop
        // A transient problem may be gone after a restart, so the choice is kept.
        Assert.NotNull(await _state.GetAsync(VideoWallpaperService.ActiveKey));
    }

    [Fact]
    public async Task A_non_recoverable_failure_gives_up_at_once_and_forgets_the_video()
    {
        var video = await ImportAsync("fatal.mp4", 11);
        await _service.ApplyAsync(video.Id);

        _host.RaiseFailure("The video data is damaged.", recoverable: false);

        await WaitUntil(() => _service.Status.State == VideoPlaybackState.Failed, "failed");
        Assert.Single(_host.Shows);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.ActiveKey));
    }

    [Fact]
    public async Task Desktop_conditions_pause_and_resume_playback_according_to_settings()
    {
        var video = await ImportAsync("policy.mp4", 12);
        await _service.ApplyAsync(video.Id);

        _host.RaiseConditions(new DesktopConditions { OnBattery = true });
        await WaitUntil(() => _host.Paused == true, "paused on battery");
        Assert.Equal(PauseReasons.OnBattery, _service.Status.PauseReasons);

        _settings.Update(s => s.VideoPauseOnBattery = false);
        await WaitUntil(() => _host.Paused == false, "resumed once the setting is off");
        Assert.Equal(VideoPlaybackState.Playing, _service.Status.State);

        _host.RaiseConditions(new DesktopConditions { FullscreenAppRunning = true });
        await WaitUntil(() => _host.Paused == true, "paused for a fullscreen app");

        _host.RaiseConditions(new DesktopConditions { CoveredDisplays = new HashSet<string> { @"\\.\DISPLAY1" } });
        await WaitUntil(() => _host.Paused == false, "one display still visible");

        _host.RaiseConditions(new DesktopConditions { CoveredDisplays = new HashSet<string> { @"\\.\DISPLAY1", @"\\.\DISPLAY2" } });
        await WaitUntil(() => _host.Paused == true, "all displays covered");
        Assert.Equal(PauseReasons.DesktopHidden, _service.Status.PauseReasons);

        _host.RaiseConditions(DesktopConditions.Normal);
        await WaitUntil(() => _host.Paused == false, "visible again");
    }

    [Fact]
    public async Task User_pause_is_persisted_and_new_videos_start_playing()
    {
        var first = await ImportAsync("p1.mp4", 13);
        var second = await ImportAsync("p2.mp4", 14);
        await _service.ApplyAsync(first.Id);

        await _service.SetUserPausedAsync(true);
        Assert.True(_host.Paused);
        Assert.Equal("true", await _state.GetAsync(VideoWallpaperService.PausedKey));

        await _service.ApplyAsync(second.Id); // choosing a video means wanting to see it
        Assert.False(_host.Paused);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.PausedKey));
    }

    [Fact]
    public async Task Changing_fit_or_display_updates_the_running_video()
    {
        var video = await ImportAsync("fit.mp4", 15);
        await _service.ApplyAsync(video.Id);

        _settings.Update(s => s.VideoFitMode = WallpaperFitMode.Stretch);
        await WaitUntil(() => _host.Shows.Count == 2, "re-show for fit");
        Assert.Equal(WallpaperFitMode.Stretch, _host.Current!.Fit);

        _settings.Update(s => s.VideoDisplayTarget = @"\\.\DISPLAY1");
        await WaitUntil(() => _host.Shows.Count == 3, "re-show for display");
        Assert.Equal([@"\\.\DISPLAY1"], _host.Current!.DisplayDeviceNames);
    }

    [Fact]
    public async Task Removing_the_active_video_stops_it_before_deleting_the_file()
    {
        var video = await ImportAsync("remove.mp4", 16);
        await _service.ApplyAsync(video.Id);

        await _service.RemoveVideoAsync(video.Id);

        Assert.Equal(1, _host.HideCalls);
        Assert.False(_service.IsActive);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.ActiveKey));
        Assert.Empty(await _library.GetAllAsync());
        Assert.False(File.Exists(video.FilePath));
    }

    [Fact]
    public async Task Removing_another_video_leaves_the_active_one_playing()
    {
        var active = await ImportAsync("active.mp4", 17);
        var other = await ImportAsync("other.mp4", 18);
        await _service.ApplyAsync(active.Id);

        await _service.RemoveVideoAsync(other.Id);

        Assert.Equal(0, _host.HideCalls);
        Assert.True(_service.IsActive);
        Assert.Single(await _library.GetAllAsync());
    }

    [Fact]
    public async Task Turning_the_video_off_keeps_its_still_frame_and_forgets_the_choice()
    {
        var video = await ImportAsync("off.mp4", 19);
        await _service.ApplyAsync(video.Id);
        await _service.DeactivateAsync();

        Assert.Null(_host.Current);
        Assert.Equal(VideoPlaybackState.Inactive, _service.Status.State);
        Assert.Null(await _state.GetAsync(VideoWallpaperService.ActiveKey));
        Assert.Single(_applier.Applied); // the still frame is not removed
    }

    [Fact]
    public async Task Scheduled_rotation_does_not_replace_an_active_video()
    {
        var video = await ImportAsync("rotation.mp4", 20);
        await _service.ApplyAsync(video.Id);
        var downloadLibrary = new DownloadLibrary(_env.Factory, NullLogger<DownloadLibrary>.Instance);
        var picture = Path.Combine(_env.Root, "rot.png");
        await File.WriteAllBytesAsync(picture, Samples.Png(32, 32));
        await downloadLibrary.ImportFileAsync(picture);
        var downloads = new DownloadService(new StubHttpClientFactory(StubHandler.Status(System.Net.HttpStatusCode.NotFound), "https://example.test/"),
            _settings, downloadLibrary, NullLogger<DownloadService>.Instance);
        var staticApply = new WallpaperApplyService(_applier, downloads, downloadLibrary, new HistoryService(_env.Factory), _env.Paths,
            NullLogger<WallpaperApplyService>.Instance, video: _service);
        _settings.Current.RotationEnabled = true;
        _settings.Current.RotationIntervalMinutes = 1;
        await using var rotation = new RotationService(_settings, downloadLibrary, staticApply, _state, NullLogger<RotationService>.Instance, _time, _service);
        var appliedBefore = _applier.Applied.Count;

        rotation.Start();
        await WaitUntil(() => rotation.NextRotationAt is not null, "scheduled");
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => _state.GetAsync(RotationService.LastRotationKey).Result is not null, "slot recorded");
        await Task.Delay(100);

        Assert.True(_service.IsActive);
        Assert.Equal(appliedBefore, _applier.Applied.Count);
        await rotation.StopAsync();
    }
}
