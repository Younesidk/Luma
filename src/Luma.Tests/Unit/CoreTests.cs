using Luma.Core.Enums;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Tests.Fakes;

namespace Luma.Tests.Unit;

public class WallpaperModelTests
{
    [Fact]
    public void Key_combines_provider_and_external_id()
    {
        var wallpaper = Samples.Wallpaper("xyz", "openverse");
        Assert.Equal("openverse:xyz", wallpaper.Key);
        Assert.Equal(Wallpaper.MakeKey("openverse", "xyz"), wallpaper.Key);
    }

    [Fact]
    public void DisplayTitle_falls_back_to_provider_and_id_when_title_missing()
    {
        Assert.Equal("Wallhaven abc", Samples.Wallpaper("abc").DisplayTitle);
        Assert.Equal("Sunset", Samples.Wallpaper("abc", title: "Sunset").DisplayTitle);
        var local = Samples.Wallpaper("1", Wallpaper.LocalProviderId) with { LocalFilePath = Path.Combine("x", "my-photo.jpg") };
        Assert.Equal("my-photo", local.DisplayTitle);
    }

    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(2560, 1600, "16:10")]
    [InlineData(3440, 1440, "2.39:1")]
    [InlineData(1080, 1920, "9:16")]
    [InlineData(1000, 1000, "1:1")]
    public void Resolution_reports_aspect_ratio(int w, int h, string expected)
    {
        Assert.Equal(expected, new WallpaperResolution(w, h).AspectLabel);
    }

    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData("3840 × 2160", 3840, 2160)]
    public void Resolution_parses_text(string text, int w, int h)
    {
        Assert.Equal(new WallpaperResolution(w, h), WallpaperResolution.TryParse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0x0")]
    [InlineData("10x")]
    public void Resolution_rejects_invalid_text(string? text)
    {
        Assert.Null(WallpaperResolution.TryParse(text));
    }

    [Fact]
    public void Search_query_paging_helpers_do_not_mutate_original()
    {
        var query = new WallpaperSearchQuery { Text = "  forest  ", Page = 2 };
        Assert.Equal("forest", query.NormalizedText);
        Assert.Equal(3, query.NextPage().Page);
        Assert.Equal(2, query.Page);
        Assert.Equal(1, query.WithPage(-5).Page);
    }

    [Fact]
    public void Search_result_has_more_depends_on_total_pages()
    {
        Assert.True(new WallpaperSearchResult { Items = [Samples.Wallpaper()], Page = 1, TotalPages = 3 }.HasMore);
        Assert.False(new WallpaperSearchResult { Items = [Samples.Wallpaper()], Page = 3, TotalPages = 3 }.HasMore);
        Assert.True(new WallpaperSearchResult { Items = [Samples.Wallpaper()], Page = 1 }.HasMore);
        Assert.False(WallpaperSearchResult.Empty().HasMore);
    }

    [Fact]
    public void Settings_normalize_clamps_out_of_range_values()
    {
        var settings = new AppSettings { ThumbnailCacheMegabytes = 1, MaxConcurrentDownloads = 99, RotationIntervalMinutes = 0, MemoryCacheItems = 1_000_000, DefaultSource = "" };
        settings.Normalize();
        Assert.Equal(32, settings.ThumbnailCacheMegabytes);
        Assert.Equal(8, settings.MaxConcurrentDownloads);
        Assert.Equal(1, settings.RotationIntervalMinutes);
        Assert.Equal(600, settings.MemoryCacheItems);
        Assert.Equal(AppSettings.AllSourcesId, settings.DefaultSource);
    }

    [Fact]
    public void Settings_migrate_moves_old_files_to_gpu_rendering_once()
    {
        var old = new AppSettings { RenderingMode = RenderingMode.Efficient, SettingsVersion = 0 };
        Assert.True(old.Migrate());
        Assert.Equal(RenderingMode.Accelerated, old.RenderingMode);
        Assert.Equal(AppSettings.CurrentVersion, old.SettingsVersion);

        // A deliberate choice made after the migration is kept.
        old.RenderingMode = RenderingMode.Efficient;
        Assert.False(old.Migrate());
        Assert.Equal(RenderingMode.Efficient, old.RenderingMode);

        Assert.Equal(RenderingMode.Accelerated, new AppSettings().RenderingMode);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(0L, "0 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(250L * 1024 * 1024, "250 MB")]
    public void Byte_sizes_are_human_readable(long? bytes, string expected) =>
        Assert.Equal(expected, ByteSizeFormatter.Format(bytes));

    [Theory]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
    [InlineData("  ...hello...  ", "hello")]
    [InlineData("CON", "_CON")]
    [InlineData("", "wallpaper")]
    [InlineData("???", "wallpaper")]
    public void Sanitize_produces_safe_names(string input, string expected) =>
        Assert.Equal(expected, SafeFileName.Sanitize(input));

    [Fact]
    public void Sanitize_limits_length()
    {
        Assert.True(SafeFileName.Sanitize(new string('a', 500)).Length <= 80);
    }

    [Fact]
    public void Download_names_avoid_collisions()
    {
        var wallpaper = Samples.Wallpaper("k1d9x2", title: "Misty Forest");
        var taken = new HashSet<string>();
        var first = SafeFileName.BuildDownloadName(wallpaper, "jpg", "dir", taken.Contains);
        taken.Add(first);
        var second = SafeFileName.BuildDownloadName(wallpaper, ".jpg", "dir", taken.Contains);

        Assert.NotEqual(first, second);
        Assert.EndsWith(".jpg", first);
        Assert.Contains("k1d9x2", first);
        Assert.Contains("(2)", second);
    }
}

public class ImageInfoReaderTests
{
    [Fact]
    public void Reads_png_dimensions()
    {
        using var stream = new MemoryStream(Samples.Png(1920, 1080));
        var info = ImageInfoReader.TryRead(stream);
        Assert.NotNull(info);
        Assert.Equal("PNG", info.Format);
        Assert.Equal(new WallpaperResolution(1920, 1080), info.Resolution);
    }

    [Fact]
    public void Reads_jpeg_dimensions_after_other_segments()
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, .. new byte[14]]); // APP0 segment
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x04, 0x38, 0x07, 0x80, .. new byte[10]]); // SOF0: 1080 x 1920
        using var stream = new MemoryStream(bytes.ToArray());
        var info = ImageInfoReader.TryRead(stream);
        Assert.NotNull(info);
        Assert.Equal("JPEG", info.Format);
        Assert.Equal(new WallpaperResolution(1920, 1080), info.Resolution);
    }

    [Fact]
    public void Reads_gif_and_bmp()
    {
        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x40, 0x01, 0xC8, 0x00, 0, 0, 0, 0 };
        Assert.Equal(new WallpaperResolution(320, 200), ImageInfoReader.TryRead(new MemoryStream(gif))!.Resolution);

        var bmp = new byte[30];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(640).CopyTo(bmp, 18);
        BitConverter.GetBytes(-480).CopyTo(bmp, 22);
        Assert.Equal(new WallpaperResolution(640, 480), ImageInfoReader.TryRead(new MemoryStream(bmp))!.Resolution);
    }

    [Fact]
    public void Rejects_html_text_and_truncated_data()
    {
        Assert.Null(ImageInfoReader.TryRead(new MemoryStream("<html><body>not an image at all</body></html>"u8.ToArray())));
        Assert.Null(ImageInfoReader.TryRead(new MemoryStream([0x89, 0x50])));
        Assert.Null(ImageInfoReader.TryRead(new MemoryStream([])));
    }
}

public class RotationSelectorTests
{
    private sealed record Item(string Key);

    private static readonly IReadOnlyList<Item> Items = [new("a"), new("b"), new("c")];

    [Fact]
    public void Returns_null_for_empty_list() =>
        Assert.Null(RotationSelector.Pick(Array.Empty<Item>(), i => i.Key, null, random: true));

    [Fact]
    public void Single_candidate_is_returned_even_if_current()
    {
        var only = new[] { new Item("a") };
        Assert.Equal("a", RotationSelector.Pick(only, i => i.Key, "a", random: true)!.Key);
    }

    [Fact]
    public void Random_never_repeats_current_when_alternatives_exist()
    {
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            Assert.NotEqual("b", RotationSelector.Pick(Items, x => x.Key, "b", random: true, rng)!.Key);
        }
    }

    [Fact]
    public void Random_eventually_visits_every_other_item()
    {
        var rng = new Random(7);
        var seen = Enumerable.Range(0, 100).Select(_ => RotationSelector.Pick(Items, x => x.Key, "a", true, rng)!.Key).ToHashSet();
        Assert.Equal(new[] { "b", "c" }, seen.OrderBy(x => x));
    }

    [Theory]
    [InlineData(null, "a")]
    [InlineData("a", "b")]
    [InlineData("c", "a")]
    [InlineData("gone", "a")]
    public void Sequential_walks_in_order_and_wraps(string? current, string expected) =>
        Assert.Equal(expected, RotationSelector.Pick(Items, x => x.Key, current, random: false)!.Key);

    [Fact]
    public void Delay_until_next_respects_last_rotation()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var interval = TimeSpan.FromMinutes(30);
        Assert.Equal(interval, RotationSelector.DelayUntilNext(null, interval, now));
        Assert.Equal(TimeSpan.FromMinutes(10), RotationSelector.DelayUntilNext(now - TimeSpan.FromMinutes(20), interval, now));
        Assert.Equal(TimeSpan.Zero, RotationSelector.DelayUntilNext(now - TimeSpan.FromHours(5), interval, now));
    }
}
