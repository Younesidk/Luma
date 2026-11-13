using System.Globalization;
using System.Text;
using System.Text.Json;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Luma.Infrastructure.Providers;

/// <summary>
/// Wallhaven integration using the official API (https://wallhaven.cc/help/api). The API key, when configured,
/// is sent in the X-API-Key header so it never shows up in URLs or logs.
/// </summary>
public sealed class WallhavenProvider : IWallpaperProvider
{
    public const string ProviderId = "wallhaven";
    public const string HttpClientName = "wallhaven";
    private const long PreviewMaxBytes = 8L * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<WallhavenProvider> _logger;
    private readonly RequestGate _gate;
    private string? _rateLimit;

    public WallhavenProvider(IHttpClientFactory httpFactory, ISettingsService settings, IOptions<LumaOptions> options,
        ILogger<WallhavenProvider> logger, TimeProvider? time = null)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
        _gate = new RequestGate(ProviderId, Math.Max(1, options.Value.Wallhaven.MaxRequestsPerMinute), TimeSpan.FromMinutes(1), time);
    }

    public string Id => ProviderId;

    public string DisplayName => "Wallhaven";

    public string? LastRateLimitInfo => _rateLimit;

    public WallpaperProviderCapabilities Capabilities { get; } = new()
    {
        SupportsPopular = true,
        SupportsRandom = true,
        SupportsDetails = true,
        Sorts =
        [
            WallpaperSort.Relevance, WallpaperSort.Newest, WallpaperSort.MostViewed,
            WallpaperSort.MostFavorited, WallpaperSort.TopRated, WallpaperSort.Random
        ],
        SupportsCategories = true,
        SupportsPurity = true,
        SupportsExplicitContent = true,
        SupportsMinimumResolution = true,
        SupportsAspectRatio = true,
        SupportsColors = true,
        SupportsApiKey = true,
        PageSize = 24,
        RateLimitSummary = "45 requests per minute"
    };

    public async Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken = default)
    {
        var hasKey = HasApiKey();
        var url = "search?" + BuildSearchQueryString(query, hasKey);
        using var doc = await GetAsync(url, cancellationToken);
        return ParseSearch(doc.RootElement, query.Page);
    }

    public Task<WallpaperSearchResult> GetPopularAsync(int page, CancellationToken cancellationToken = default) =>
        SearchAsync(new WallpaperSearchQuery { Sort = WallpaperSort.TopRated, Page = page }, cancellationToken);

    public Task<WallpaperSearchResult> GetRandomAsync(int page, string? seed, CancellationToken cancellationToken = default) =>
        SearchAsync(new WallpaperSearchQuery { Sort = WallpaperSort.Random, Page = page, Seed = seed }, cancellationToken);

    public async Task<Wallpaper?> GetDetailsAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId) || !externalId.All(char.IsAsciiLetterOrDigit)) return null;
        try
        {
            using var doc = await GetAsync($"w/{externalId}", cancellationToken);
            var data = doc.RootElement.Get("data");
            return data is null ? null : MapWallpaper(data.Value, includeDetails: true);
        }
        catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>Translates a neutral query into Wallhaven's documented query-string parameters.</summary>
    public static string BuildSearchQueryString(WallpaperSearchQuery query, bool hasApiKey)
    {
        var parameters = new List<(string Key, string Value)>();

        var text = query.NormalizedText;
        if (text.Length > 0) parameters.Add(("q", text));

        var categories = query.Categories == WallpaperCategories.None ? WallpaperCategories.All : query.Categories;
        parameters.Add(("categories", Bit(categories, WallpaperCategories.General) + Bit(categories, WallpaperCategories.Anime) + Bit(categories, WallpaperCategories.People)));

        var purity = query.Purity;
        if (!hasApiKey) purity &= ~ContentPurity.Explicit; // NSFW requires an API key
        if (purity == ContentPurity.None) purity = ContentPurity.Safe;
        parameters.Add(("purity", Bit(purity, ContentPurity.Safe) + Bit(purity, ContentPurity.Sketchy) + Bit(purity, ContentPurity.Explicit)));

        var sorting = query.Sort switch
        {
            // "Best match" without keywords means "what is popular" – there is nothing to be relevant to.
            WallpaperSort.Relevance => text.Length == 0 ? "toplist" : "relevance",
            WallpaperSort.Newest => "date_added",
            WallpaperSort.MostViewed => "views",
            WallpaperSort.MostFavorited => "favorites",
            WallpaperSort.TopRated => "toplist",
            WallpaperSort.Random => "random",
            _ => "date_added"
        };
        parameters.Add(("sorting", sorting));
        parameters.Add(("order", "desc"));
        if (sorting == "toplist") parameters.Add(("topRange", MapTopRange(query.TopRange)));

        switch (query.MinimumResolution)
        {
            case MinimumResolution.FullHd: parameters.Add(("atleast", "1920x1080")); break;
            case MinimumResolution.QuadHd: parameters.Add(("atleast", "2560x1440")); break;
            case MinimumResolution.UltraHd4K: parameters.Add(("atleast", "3840x2160")); break;
        }

        var ratios = query.AspectRatio switch
        {
            AspectRatioFilter.Wide16x9 => "16x9",
            AspectRatioFilter.Wide16x10 => "16x10",
            AspectRatioFilter.UltraWide21x9 => "21x9,32x9",
            AspectRatioFilter.Portrait => "9x16,10x16",
            AspectRatioFilter.Square => "1x1",
            _ => null
        };
        if (ratios is not null) parameters.Add(("ratios", ratios));

        var color = query.Colors.Select(c => c.TrimStart('#').ToLowerInvariant()).FirstOrDefault(c => c.Length == 6 && c.All(Uri.IsHexDigit));
        if (color is not null) parameters.Add(("colors", color));

        if (query.Page > 1) parameters.Add(("page", query.Page.ToString(CultureInfo.InvariantCulture)));
        if (sorting == "random" && !string.IsNullOrEmpty(query.Seed) && query.Seed.All(char.IsAsciiLetterOrDigit))
            parameters.Add(("seed", query.Seed));

        return string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    public static WallpaperSearchResult ParseSearch(JsonElement root, int requestedPage)
    {
        var items = new List<Wallpaper>();
        foreach (var element in root.GetArray("data"))
        {
            var mapped = MapWallpaper(element, includeDetails: false);
            if (mapped is not null) items.Add(mapped);
        }

        var meta = root.Get("meta");
        var currentPage = meta?.GetInt("current_page") ?? requestedPage;
        var lastPage = meta?.GetInt("last_page");
        return new WallpaperSearchResult
        {
            Items = items,
            Page = currentPage,
            TotalPages = lastPage,
            TotalItems = meta?.GetInt("total"),
            Seed = meta?.GetString("seed")
        };
    }

    public static Wallpaper? MapWallpaper(JsonElement e, bool includeDetails)
    {
        var id = e.GetString("id");
        if (id is null) return null;

        var thumbs = e.Get("thumbs");
        var large = thumbs?.GetString("large");
        var original = thumbs?.GetString("original");
        var small = thumbs?.GetString("small");
        var full = e.GetString("path");

        var width = e.GetInt("dimension_x");
        var height = e.GetInt("dimension_y");
        var resolution = width is > 0 && height is > 0
            ? new WallpaperResolution(width.Value, height.Value)
            : WallpaperResolution.TryParse(e.GetString("resolution"));

        var tags = includeDetails
            ? e.GetArray("tags").Select(t => t.GetString("name")).Where(n => n is not null).Cast<string>().ToList()
            : [];
        var uploader = e.Get("uploader")?.GetString("username");

        return new Wallpaper
        {
            ProviderId = ProviderId,
            ExternalId = id,
            ThumbnailUrl = large ?? small ?? original,
            // The "original" thumbnail is tiny, so the preview uses the real file unless it is very large.
            PreviewUrl = full is not null && e.GetLong("file_size") is > 0 and <= PreviewMaxBytes ? full : original ?? large ?? full,
            FullUrl = full,
            SourcePageUrl = e.GetString("url") ?? $"https://wallhaven.cc/w/{id}",
            Resolution = resolution,
            FileSizeBytes = e.GetLong("file_size"),
            FileType = e.GetString("file_type"),
            CreatedAt = ParseDate(e.GetString("created_at")),
            Metadata = new WallpaperMetadata
            {
                CreatorName = uploader,
                CreatorUrl = uploader is null ? null : $"https://wallhaven.cc/user/{Uri.EscapeDataString(uploader)}",
                Category = e.GetString("category"),
                Purity = e.GetString("purity"),
                ViewCount = e.GetInt("views"),
                FavoriteCount = e.GetInt("favorites"),
                Colors = e.GetArray("colors").Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null).Where(c => c is not null).Cast<string>().ToList(),
                Tags = tags,
                UpstreamSource = e.GetString("source")
            }
        };
    }

    private async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Accept.ParseAdd("application/json");
        var key = _settings.Current.WallhavenApiKey;
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("X-API-Key", key.Trim());

        return await ProviderHttp.GetJsonAsync(client, request, _gate, ProviderId, _logger, response =>
        {
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
            {
                var limit = response.Headers.TryGetValues("X-RateLimit-Limit", out var l) ? l.FirstOrDefault() : "45";
                _rateLimit = $"{remaining.FirstOrDefault()} of {limit} requests left this minute";
            }
        }, ct);
    }

    private bool HasApiKey() => !string.IsNullOrWhiteSpace(_settings.Current.WallhavenApiKey);

    private static string Bit(WallpaperCategories value, WallpaperCategories flag) => (value & flag) != 0 ? "1" : "0";

    private static string Bit(ContentPurity value, ContentPurity flag) => (value & flag) != 0 ? "1" : "0";

    private static string MapTopRange(TopRange range) => range switch
    {
        TopRange.Day => "1d",
        TopRange.ThreeDays => "3d",
        TopRange.Week => "1w",
        TopRange.Month => "1M",
        TopRange.ThreeMonths => "3M",
        TopRange.SixMonths => "6M",
        TopRange.Year => "1y",
        _ => "1M"
    };

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
}
