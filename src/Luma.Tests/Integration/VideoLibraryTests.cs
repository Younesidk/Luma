using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Infrastructure.Videos;
using Luma.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luma.Tests.Integration;

/// <summary>
/// Import / removal against a real SQLite database and the real file system. The playback engine is faked here;
/// WindowsVideoTests covers real decoding.
/// </summary>
public class VideoLibraryTests
{
    private static (VideoLibrary Library, FakeVideoProbe Probe) Create(TestEnvironment env)
    {
        var probe = new FakeVideoProbe();
        return (new VideoLibrary(env.Factory, probe, env.Paths, NullLogger<VideoLibrary>.Instance), probe);
    }

    private static string SourceDir(TestEnvironment env) => Path.Combine(env.Root, "user-files");

    [Fact]
    public async Task Importing_copies_into_the_managed_folder_and_leaves_the_original_alone()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, probe) = Create(env);
        var original = VideoSamples.Mp4Bytes(seed: 7);
        var source = VideoSamples.Write(SourceDir(env), "Ocean Waves.mp4", original);
        var progress = new List<double>();

        var result = await library.ImportAsync(source, new SynchronousProgress(progress.Add));

        Assert.False(result.AlreadyExisted);
        var video = result.Video;
        Assert.Equal("Ocean Waves", video.Title);
        Assert.StartsWith(env.Paths.VideosDirectory, video.FilePath);
        Assert.EndsWith(".mp4", video.FilePath);
        Assert.Equal(original, File.ReadAllBytes(video.FilePath));
        Assert.Equal(original, File.ReadAllBytes(source));               // never modified
        Assert.Equal(source, video.OriginalPath);
        Assert.Equal(original.Length, video.FileSizeBytes);
        Assert.Equal(64, video.Sha256!.Length);
        Assert.True(File.Exists(video.ThumbnailPath));
        Assert.True(File.Exists(video.PosterPath));
        Assert.Equal(1920, video.Info.Width);
        Assert.Equal(TimeSpan.FromSeconds(12), video.Info.Duration);
        Assert.Equal(VideoContainer.Mp4, video.Info.Container);
        Assert.Equal("local", video.Wallpaper.ProviderId);
        Assert.Equal("video/mp4", video.Wallpaper.FileType);
        Assert.True(video.Wallpaper.IsVideo);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1.0, progress[^1]);
        Assert.Equal(progress.OrderBy(p => p), progress);                 // monotonic

        // Restart: a new library instance reads the same entry back.
        var (reloaded, _) = Create(env);
        var all = await reloaded.GetAllAsync();
        Assert.Equal(video.Id, Assert.Single(all).Id);
        Assert.True(all[0].FileExists);
    }

    [Fact]
    public async Task Identical_content_is_not_imported_twice_even_under_another_name()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, probe) = Create(env);
        var bytes = VideoSamples.Mp4Bytes(seed: 3);
        var first = await library.ImportAsync(VideoSamples.Write(SourceDir(env), "a.mp4", bytes));
        var second = await library.ImportAsync(VideoSamples.Write(SourceDir(env), "copy of a.m4v", bytes));

        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.Video.Id, second.Video.Id);
        Assert.Equal(1, probe.Calls);
        Assert.Single(await library.GetAllAsync());
        Assert.Single(Directory.GetFiles(env.Paths.VideosDirectory));
    }

    [Fact]
    public async Task A_webm_with_the_wrong_extension_is_stored_with_the_right_one()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, _) = Create(env);
        var result = await library.ImportAsync(VideoSamples.Write(SourceDir(env), "mislabelled.mp4", VideoSamples.WebMBytes()));
        Assert.EndsWith(".webm", result.Video.FilePath);
        Assert.Equal(VideoContainer.WebM, result.Video.Info.Container);
        Assert.Equal("video/webm", result.Video.Wallpaper.FileType);
    }

    [Fact]
    public async Task Unsupported_or_fake_files_are_rejected_before_anything_is_copied()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, probe) = Create(env);

        await Assert.ThrowsAsync<VideoNotPlayableException>(() => library.ImportAsync(VideoSamples.Write(SourceDir(env), "clip.avi", VideoSamples.Mp4Bytes())));
        await Assert.ThrowsAsync<VideoNotPlayableException>(() => library.ImportAsync(VideoSamples.Write(SourceDir(env), "notes.mp4", "plain text"u8.ToArray())));
        await Assert.ThrowsAsync<VideoNotPlayableException>(() => library.ImportAsync(Path.Combine(SourceDir(env), "missing.mp4")));

        Assert.Equal(0, probe.Calls);
        Assert.False(Directory.Exists(env.Paths.VideosDirectory) && Directory.EnumerateFiles(env.Paths.VideosDirectory, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_video_the_player_cannot_decode_is_rejected_and_its_copy_removed()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, probe) = Create(env);
        probe.FailWith = "Windows doesn't have a decoder for this video.";

        var ex = await Assert.ThrowsAsync<VideoNotPlayableException>(() => library.ImportAsync(VideoSamples.Write(SourceDir(env), "hevc.mp4", VideoSamples.Mp4Bytes())));

        Assert.Contains("\"hevc.mp4\" can't be played", ex.Message);
        Assert.Contains("decoder", ex.Message);
        Assert.Empty(await library.GetAllAsync());
        Assert.Empty(Directory.EnumerateFiles(env.Paths.VideosDirectory, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(SourceDir(env), "hevc.mp4")));
    }

    [Fact]
    public async Task Cancelling_an_import_leaves_no_partial_files()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, probe) = Create(env);
        probe.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var import = library.ImportAsync(VideoSamples.Write(SourceDir(env), "slow.mp4", VideoSamples.Mp4Bytes(size: 3_000_000)), cancellationToken: cts.Token);
        while (probe.Calls == 0) await Task.Delay(10);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        Assert.Empty(await library.GetAllAsync());
        Assert.Empty(Directory.EnumerateFiles(env.Paths.VideosDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Removing_deletes_managed_files_only()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, _) = Create(env);
        var source = VideoSamples.Write(SourceDir(env), "keep-me.mp4", VideoSamples.Mp4Bytes());
        var video = (await library.ImportAsync(source)).Video;
        var changed = 0;
        library.Changed += (_, _) => changed++;

        await library.RemoveAsync(video.Id);

        Assert.Empty(await library.GetAllAsync());
        Assert.False(File.Exists(video.FilePath));
        Assert.False(File.Exists(video.ThumbnailPath));
        Assert.False(File.Exists(video.PosterPath));
        Assert.True(File.Exists(source));
        Assert.Equal(1, changed);

        await library.RemoveAsync(video.Id); // removing twice is harmless
    }

    [Fact]
    public async Task A_path_outside_the_managed_folder_is_never_deleted()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, _) = Create(env);
        var video = (await library.ImportAsync(VideoSamples.Write(SourceDir(env), "x.mp4", VideoSamples.Mp4Bytes()))).Video;
        var outside = VideoSamples.Write(SourceDir(env), "precious.mp4", VideoSamples.Mp4Bytes(seed: 99));

        // Simulate a hand-edited / corrupted database row pointing at a user file.
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            var row = db.VideoWallpapers.Single(v => v.Id == video.Id);
            row.FilePath = outside;
            await db.SaveChangesAsync();
        }

        await library.RemoveAsync(video.Id);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task A_deleted_managed_file_is_reported_and_re_importing_repairs_the_entry()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, _) = Create(env);
        var source = VideoSamples.Write(SourceDir(env), "clip.mp4", VideoSamples.Mp4Bytes(seed: 5));
        var video = (await library.ImportAsync(source)).Video;

        File.Delete(video.FilePath);
        var missing = Assert.Single(await library.GetAllAsync());
        Assert.False(missing.FileExists);

        var repaired = await library.ImportAsync(source);
        Assert.False(repaired.AlreadyExisted);
        Assert.Equal(video.Id, repaired.Video.Id);
        Assert.True(Assert.Single(await library.GetAllAsync()).FileExists);
    }

    [Fact]
    public async Task Moving_or_deleting_the_original_after_import_changes_nothing()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, _) = Create(env);
        var source = VideoSamples.Write(SourceDir(env), "temp.mp4", VideoSamples.Mp4Bytes());
        var video = (await library.ImportAsync(source)).Video;
        File.Delete(source);

        var stored = await library.GetAsync(video.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.FileExists);
    }

    [Fact]
    public async Task Cleanup_removes_interrupted_imports_and_unreferenced_files_but_nothing_else()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, _) = Create(env);
        var kept = (await library.ImportAsync(VideoSamples.Write(SourceDir(env), "kept.mp4", VideoSamples.Mp4Bytes()))).Video;

        var dir = env.Paths.VideosDirectory;
        var staging = VideoSamples.Write(Path.Combine(dir, ".importing"), "abc.tmp", [1, 2, 3]);
        var orphanVideo = VideoSamples.Write(dir, "0123456789abcdef0123.mp4", [1]);
        var orphanThumb = VideoSamples.Write(Path.Combine(dir, "Thumbnails"), "0123456789abcdef0123.png", [1]);
        var userFile = VideoSamples.Write(dir, "my holiday.mp4", [1]);   // not a name Luma creates
        var otherFile = VideoSamples.Write(dir, "readme.txt", [1]);

        await library.CleanupAsync();

        Assert.False(File.Exists(staging));
        Assert.False(File.Exists(orphanVideo));
        Assert.False(File.Exists(orphanThumb));
        Assert.True(File.Exists(userFile));
        Assert.True(File.Exists(otherFile));
        Assert.True(File.Exists(kept.FilePath));
        Assert.True(File.Exists(kept.ThumbnailPath));
        Assert.True(File.Exists(kept.PosterPath));
    }

    [Fact]
    public async Task Concurrent_imports_of_the_same_file_produce_one_entry()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, _) = Create(env);
        var source = VideoSamples.Write(SourceDir(env), "race.mp4", VideoSamples.Mp4Bytes(seed: 11));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => library.ImportAsync(source)));

        Assert.Single(await library.GetAllAsync());
        Assert.Single(results, r => !r.AlreadyExisted);
        Assert.Single(results.Select(r => r.Video.Id).Distinct());
    }

    [Fact]
    public async Task Unsupported_platforms_refuse_imports_with_their_reason()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var (library, probe) = Create(env);
        probe.Capabilities = new Core.Models.VideoHostCapabilities(false, "Live video wallpapers need Windows 10 or 11.");
        var ex = await Assert.ThrowsAsync<VideoNotPlayableException>(() => library.ImportAsync(VideoSamples.Write(SourceDir(env), "a.mp4", VideoSamples.Mp4Bytes())));
        Assert.Contains("Windows 10 or 11", ex.Message);
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need every report in order.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
