using System.Net;
using System.Text.Json;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Models;
using Luma.Infrastructure.Providers;
using Luma.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Luma.Tests.Unit;

public class WallhavenProviderTests
{
    private const string SearchJson = """
        {
          "data": [
            {
              "id": "8g8q32", "url": "https://wallhaven.cc/w/8g8q32", "short_url": "https://whvn.cc/8g8q32",
              "views": 11565, "favorites": 118, "source": "", "purity": "sfw", "category": "general",
              "dimension_x": 1920, "dimension_y": 1080, "resolution": "1920x1080", "ratio": "1.78",
              "file_size": 198479, "file_type": "image/jpeg", "created_at": "2026-10-18 23:02:17",
              "colors": ["#999999", "#000000"],
              "path": "https://w.wallhaven.cc/full/8g/wallhaven-8g8q32.jpg",
              "thumbs": { "large": "https://th.wallhaven.cc/lg/8g/8g8q32.jpg", "original": "https://th.wallhaven.cc/orig/8g/8g8q32.jpg", "small": "https://th.wallhaven.cc/small/8g/8g8q32.jpg" }
            },
            {
              "id": "qrpkjr", "url": "https://wallhaven.cc/w/qrpkjr", "purity": "sketchy", "category": "anime",
              "dimension_x": 5120, "dimension_y": 3152, "file_size": 39000000, "file_type": "image/png",
              "path": "https://w.wallhaven.cc/full/qr/wallhaven-qrpkjr.png",
              "thumbs": { "large": "https://th.wallhaven.cc/lg/qr/qrpkjr.jpg", "original": "https://th.wallhaven.cc/orig/qr/qrpkjr.jpg" }
            }
          ],
          "meta": { "current_page": 1, "last_page": 4, "per_page": "24", "total": 96, "query": null, "seed": null }
        }
        """;

    [Fact]
    public async Task Search_maps_wallpapers_and_pagination()
    {
        var handler = StubHandler.Json(SearchJson);
        var provider = Providers.Wallhaven(handler);

        var result = await provider.SearchAsync(new WallpaperSearchQuery { Text = "nature" });

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, result.Page);
        Assert.Equal(4, result.TotalPages);
        Assert.Equal(96, result.TotalItems);
        Assert.True(result.HasMore);

        var first = result.Items[0];
        Assert.Equal("wallhaven:8g8q32", first.Key);
        Assert.Equal(new WallpaperResolution(1920, 1080), first.Resolution);
        Assert.Equal("https://th.wallhaven.cc/lg/8g/8g8q32.jpg", first.ThumbnailUrl);
        Assert.Equal("https://w.wallhaven.cc/full/8g/wallhaven-8g8q32.jpg", first.FullUrl);
        Assert.Equal("https://w.wallhaven.cc/full/8g/wallhaven-8g8q32.jpg", first.PreviewUrl); // small file: use real image
        Assert.Equal(198479, first.FileSizeBytes);
        Assert.Equal(new[] { "#999999", "#000000" }, first.Metadata.Colors);
        Assert.Equal(11565, first.Metadata.ViewCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 23, 2, 17, TimeSpan.Zero), first.CreatedAt);
    }

    [Fact]
    public async Task Large_files_use_the_thumbnail_for_preview_and_missing_metadata_is_tolerated()
    {
        var provider = Providers.Wallhaven(StubHandler.Json(SearchJson));
        var second = (await provider.SearchAsync(new WallpaperSearchQuery())).Items[1];

        Assert.Equal("https://th.wallhaven.cc/orig/qr/qrpkjr.jpg", second.PreviewUrl);
        Assert.Empty(second.Metadata.Colors);
        Assert.Null(second.Metadata.CreatorName);
        Assert.Null(second.CreatedAt);
        Assert.Equal("sketchy", second.Metadata.Purity);
    }

    [Fact]
    public async Task Empty_results_are_not_an_error()
    {
        var provider = Providers.Wallhaven(StubHandler.Json("""{"data":[],"meta":{"current_page":1,"last_page":1,"total":0}}"""));
        var result = await provider.SearchAsync(new WallpaperSearchQuery { Text = "zzzzzz" });
        Assert.Empty(result.Items);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Invalid_json_becomes_invalid_response_error()
    {
        var provider = Providers.Wallhaven(StubHandler.Json("<html>oops"));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task Rate_limit_response_is_reported_and_blocks_follow_up_requests()
    {
        var handler = StubHandler.Status(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));
        var provider = Providers.Wallhaven(handler);

        var first = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.RateLimited, first.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), first.RetryAfter);

        var second = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.RateLimited, second.Kind);
        Assert.Single(handler.Requests); // the second call never hit the network
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ProviderErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, ProviderErrorKind.ServerError)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, ProviderErrorKind.NotFound)]
    public async Task Http_failures_are_classified(HttpStatusCode status, ProviderErrorKind expected)
    {
        var provider = Providers.Wallhaven(StubHandler.Status(status));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(expected, ex.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task Network_failure_becomes_friendly_network_error()
    {
        var provider = Providers.Wallhaven(StubHandler.Throwing(new HttpRequestException("No such host is known.")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.Network, ex.Kind);
        Assert.DoesNotContain("No such host", ex.Message);
    }

    [Fact]
    public async Task Timeout_is_reported_separately_from_cancellation()
    {
        var provider = Providers.Wallhaven(StubHandler.Throwing(new TaskCanceledException("timeout")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.Timeout, ex.Kind);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelling = Providers.Wallhaven(new StubHandler(_ => throw new OperationCanceledException(cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.SearchAsync(new WallpaperSearchQuery(), cts.Token));
    }

    [Fact]
    public async Task Api_key_travels_in_header_never_in_url()
    {
        var settings = new FakeSettings();
        settings.Current.WallhavenApiKey = "SECRET-KEY-123";
        var handler = StubHandler.Json(SearchJson);
        var provider = Providers.Wallhaven(handler, settings);

        await provider.SearchAsync(new WallpaperSearchQuery { Text = "x", Purity = ContentPurity.Safe | ContentPurity.Explicit });

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain("SECRET-KEY-123", request.RequestUri!.ToString());
        Assert.Equal("SECRET-KEY-123", request.Headers.GetValues("X-API-Key").Single());
        Assert.Contains("purity=101", request.RequestUri.Query);
    }

    [Fact]
    public void Explicit_purity_is_dropped_without_api_key()
    {
        var query = Wallhaven(new WallpaperSearchQuery { Purity = ContentPurity.Safe | ContentPurity.Sketchy | ContentPurity.Explicit }, hasKey: false);
        Assert.Equal("110", query["purity"]);
    }

    [Fact]
    public void Query_string_covers_documented_parameters()
    {
        var query = Wallhaven(new WallpaperSearchQuery
        {
            Text = "dark forest",
            Page = 3,
            Sort = WallpaperSort.TopRated,
            TopRange = TopRange.Week,
            Categories = WallpaperCategories.General | WallpaperCategories.People,
            MinimumResolution = MinimumResolution.QuadHd,
            AspectRatio = AspectRatioFilter.Wide16x9,
            Colors = ["#336600"]
        }, hasKey: false);

        Assert.Equal("dark forest", query["q"]);
        Assert.Equal("101", query["categories"]);
        Assert.Equal("toplist", query["sorting"]);
        Assert.Equal("1w", query["topRange"]);
        Assert.Equal("2560x1440", query["atleast"]);
        Assert.Equal("16x9", query["ratios"]);
        Assert.Equal("336600", query["colors"]);
        Assert.Equal("3", query["page"]);
    }

    [Fact]
    public void Relevance_without_keywords_means_toplist_and_random_carries_seed()
    {
        Assert.Equal("toplist", Wallhaven(new WallpaperSearchQuery { Sort = WallpaperSort.Relevance }, false)["sorting"]);
        Assert.Equal("relevance", Wallhaven(new WallpaperSearchQuery { Sort = WallpaperSort.Relevance, Text = "cat" }, false)["sorting"]);
        var random = Wallhaven(new WallpaperSearchQuery { Sort = WallpaperSort.Random, Seed = "abc123" }, false);
        Assert.Equal("random", random["sorting"]);
        Assert.Equal("abc123", random["seed"]);
        Assert.False(Wallhaven(new WallpaperSearchQuery { Sort = WallpaperSort.Random, Seed = "bad seed!" }, false).ContainsKey("seed"));
    }

    [Fact]
    public async Task Details_include_tags_and_uploader()
    {
        const string details = """
            {"data":{"id":"abc","url":"https://wallhaven.cc/w/abc","dimension_x":3000,"dimension_y":2000,"file_size":1000,"path":"https://w.wallhaven.cc/full/ab/wallhaven-abc.jpg",
            "uploader":{"username":"someone"},"thumbs":{"large":"https://t/l.jpg"},"tags":[{"name":"forest"},{"name":"mist"}]}}
            """;
        var provider = Providers.Wallhaven(StubHandler.Json(details));
        var wallpaper = await provider.GetDetailsAsync("abc");

        Assert.NotNull(wallpaper);
        Assert.Equal("someone", wallpaper.Metadata.CreatorName);
        Assert.Equal(new[] { "forest", "mist" }, wallpaper.Metadata.Tags);
    }

    [Fact]
    public async Task Details_return_null_for_unknown_or_unsafe_ids()
    {
        Assert.Null(await Providers.Wallhaven(StubHandler.Status(HttpStatusCode.NotFound)).GetDetailsAsync("nope"));
        var handler = StubHandler.Json("{}");
        Assert.Null(await Providers.Wallhaven(handler).GetDetailsAsync("../../etc"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Client_side_limiter_rejects_bursts_beyond_the_window()
    {
        var time = new FakeTimeProvider();
        var handler = StubHandler.Json(SearchJson);
        var provider = Providers.Wallhaven(handler, time: time);

        for (var i = 0; i < 40; i++) await provider.SearchAsync(new WallpaperSearchQuery());
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.RateLimited, ex.Kind);
        Assert.Equal(40, handler.Requests.Count);

        time.Advance(TimeSpan.FromMinutes(1.1));
        await provider.SearchAsync(new WallpaperSearchQuery());
    }

    private static Dictionary<string, string> Wallhaven(WallpaperSearchQuery query, bool hasKey) =>
        WallhavenProvider.BuildSearchQueryString(query, hasKey).Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
}

public class OpenverseProviderTests
{
    private const string SearchJson = """
        {
          "result_count": 240, "page_count": 120, "page_size": 20, "page": 1,
          "results": [
            {
              "id": "c6a260ef-8a37-43df-939e-f3d662403fcc", "title": "Transantarctic mountain hg",
              "indexed_on": "2020-09-09T14:32:44.221958Z",
              "foreign_landing_url": "https://commons.wikimedia.org/w/index.php?curid=585111",
              "url": "https://upload.wikimedia.org/wikipedia/commons/c/c1/Transantarctic_mountain_hg.jpg",
              "creator": "Hannes Grobe", "creator_url": "https://commons.wikimedia.org/wiki/User:Hgrobe",
              "license": "by-sa", "license_version": "2.5", "license_url": "https://creativecommons.org/licenses/by-sa/2.5/",
              "provider": "wikimedia", "source": "wikimedia", "category": null, "filesize": 478522, "filetype": "jpg",
              "tags": [{"name":"mountain"},{"name":"snow"}],
              "attribution": "\"Transantarctic mountain hg\" by Hannes Grobe is licensed under CC BY-SA 2.5.",
              "width": 2563, "height": 1652,
              "thumbnail": "https://api.openverse.org/v1/images/c6a260ef-8a37-43df-939e-f3d662403fcc/thumb/"
            },
            {
              "id": "11111111-2222-3333-4444-555555555555", "title": null, "url": "https://example.org/huge.png",
              "license": "cc0", "license_version": "1.0", "filesize": 90000000, "filetype": "png", "width": null, "height": null
            }
          ]
        }
        """;

    [Fact]
    public async Task Search_maps_license_creator_and_attribution()
    {
        var provider = Providers.Openverse(StubHandler.Json(SearchJson));
        var result = await provider.SearchAsync(new WallpaperSearchQuery { Text = "mountain" });

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, result.Page);
        var item = result.Items[0];
        Assert.Equal("openverse:c6a260ef-8a37-43df-939e-f3d662403fcc", item.Key);
        Assert.Equal("Transantarctic mountain hg", item.Title);
        Assert.Equal("Hannes Grobe", item.Metadata.CreatorName);
        Assert.Equal("CC BY-SA 2.5", item.Metadata.LicenseName);
        Assert.Equal("https://creativecommons.org/licenses/by-sa/2.5/", item.Metadata.LicenseUrl);
        Assert.Contains("licensed under CC BY-SA 2.5", item.Metadata.Attribution);
        Assert.True(item.Metadata.RequiresAttribution);
        Assert.Equal(new WallpaperResolution(2563, 1652), item.Resolution);
        Assert.Equal("image/jpeg", item.FileType);
        Assert.Equal(item.FullUrl, item.PreviewUrl); // modest original => used for preview
        Assert.EndsWith("/thumb/", item.ThumbnailUrl);
        Assert.Equal(new[] { "mountain", "snow" }, item.Metadata.Tags);
    }

    [Fact]
    public async Task Missing_metadata_and_huge_originals_are_handled()
    {
        var provider = Providers.Openverse(StubHandler.Json(SearchJson));
        var sparse = (await provider.SearchAsync(new WallpaperSearchQuery())).Items[1];

        Assert.Null(sparse.Title);
        Assert.Null(sparse.Resolution);
        Assert.Null(sparse.Metadata.CreatorName);
        Assert.Equal("CC0 1.0", sparse.Metadata.LicenseName);
        Assert.Equal("https://example.org/huge.png", sparse.ThumbnailUrl); // no thumbnail supplied: fall back
        Assert.Contains("Openverse", sparse.DisplayTitle);
    }

    [Fact]
    public async Task Anonymous_pagination_is_capped_at_documented_depth()
    {
        var handler = StubHandler.Json(SearchJson);
        var provider = Providers.Openverse(handler);

        var result = await provider.SearchAsync(new WallpaperSearchQuery());
        Assert.Equal(12, result.TotalPages); // 240 / 20

        var beyond = await provider.SearchAsync(new WallpaperSearchQuery { Page = 13 });
        Assert.Empty(beyond.Items);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_on_deep_page_means_end_of_results_not_an_error()
    {
        var provider = Providers.Openverse(StubHandler.Status(HttpStatusCode.Unauthorized));
        var result = await provider.SearchAsync(new WallpaperSearchQuery { Page = 5 });
        Assert.Empty(result.Items);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Empty_results_invalid_json_and_rate_limits()
    {
        var empty = await Providers.Openverse(StubHandler.Json("""{"result_count":0,"page_count":0,"page_size":20,"page":1,"results":[]}""")).SearchAsync(new WallpaperSearchQuery { Text = "x" });
        Assert.Empty(empty.Items);

        var invalid = await Assert.ThrowsAsync<ProviderException>(() => Providers.Openverse(StubHandler.Json("nope")).SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.InvalidResponse, invalid.Kind);

        var limited = await Assert.ThrowsAsync<ProviderException>(() => Providers.Openverse(StubHandler.Status(HttpStatusCode.TooManyRequests)).SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.RateLimited, limited.Kind);

        var offline = await Assert.ThrowsAsync<ProviderException>(() => Providers.Openverse(StubHandler.Throwing(new HttpRequestException("boom"))).SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.Network, offline.Kind);

        var gatewayTimeout = await Assert.ThrowsAsync<ProviderException>(() => Providers.Openverse(StubHandler.Status(HttpStatusCode.GatewayTimeout)).SearchAsync(new WallpaperSearchQuery()));
        Assert.Equal(ProviderErrorKind.ServerError, gatewayTimeout.Kind);
    }

    [Fact]
    public void Query_string_translates_filters()
    {
        var qs = OpenverseProvider.BuildSearchQueryString(new WallpaperSearchQuery
        {
            Text = "sunset beach",
            Page = 2,
            Sort = WallpaperSort.Newest,
            ContentKind = ContentKind.Photograph,
            AspectRatio = AspectRatioFilter.UltraWide21x9,
            MinimumResolution = MinimumResolution.FullHd,
            License = LicenseFilter.Commercial
        });
        var query = qs.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        Assert.Equal("sunset beach", query["q"]);
        Assert.Equal("2", query["page"]);
        Assert.Equal("20", query["page_size"]);
        Assert.Equal("photograph", query["category"]);
        Assert.Equal("wide", query["aspect_ratio"]);
        Assert.Equal("large", query["size"]);
        Assert.Equal("commercial", query["license_type"]);
        Assert.Equal("indexed_on", query["sort_by"]);
        Assert.Equal("false", query["mature"]);
    }

    [Fact]
    public async Task Client_credentials_are_exchanged_for_a_bearer_token_once()
    {
        var settings = new FakeSettings();
        settings.Current.OpenverseClientId = "id";
        settings.Current.OpenverseClientSecret = "secret";
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/token/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"tok123","expires_in":36000,"token_type":"Bearer"}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchJson) });
        var provider = Providers.Openverse(handler, settings);

        await provider.SearchAsync(new WallpaperSearchQuery());
        await provider.SearchAsync(new WallpaperSearchQuery { Page = 2 });

        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/token/", StringComparison.Ordinal)));
        var search = handler.Requests.Where(r => r.RequestUri!.AbsolutePath.Contains("/images/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, search.Count);
        Assert.All(search, r => Assert.Equal("tok123", r.Headers.Authorization!.Parameter));
        Assert.All(search, r => Assert.DoesNotContain("secret", r.RequestUri!.ToString()));
    }

    [Theory]
    [InlineData("by", "4.0", "CC BY 4.0")]
    [InlineData("by-nc-nd", "3.0", "CC BY-NC-ND 3.0")]
    [InlineData("cc0", null, "CC0 1.0")]
    [InlineData("pdm", "1.0", "Public Domain Mark 1.0")]
    [InlineData(null, null, null)]
    public void License_names_are_formatted(string? code, string? version, string? expected) =>
        Assert.Equal(expected, OpenverseProvider.FormatLicense(code, version));

    [Fact]
    public void Malformed_items_are_skipped_without_failing_the_page()
    {
        using var doc = JsonDocument.Parse("""{"results":[{"title":"no id"},{"id":"a1","url":"https://x/y.jpg"}],"page_count":1}""");
        var result = OpenverseProvider.ParseSearch(doc.RootElement, 1);
        Assert.Single(result.Items);
    }
}
