using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Luma.Infrastructure.Providers;

/// <summary>
/// Openverse integration (https://api.openverse.org/v1/). Results are openly licensed images, so licence and
/// attribution data are carried through to the UI. Anonymous access is rate limited and paginates up to 240 results;
/// optional OAuth2 client credentials (configured in Settings) lift those limits.
/// </summary>
public sealed class OpenverseProvider : IWallpaperProvider
{
    public const string ProviderId = "openverse";
    public const string HttpClientName = "openverse";
    public const int AnonymousPaginationDepth = 240;
    private const int PageSize = 20;
    private const long MaxPreviewBytes = 8L * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<OpenverseProvider> _logger;
    private readonly RequestGate _gate;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private string? _tokenFor;
    private DateTimeOffset _tokenExpiry;
    private string? _rateLimit;

    public OpenverseProvider(IHttpClientFactory httpFactory, ISettingsService settings, IOptions<LumaOptions> options,
        ILogger<OpenverseProvider> logger, TimeProvider? time = null)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _gate = new RequestGate(ProviderId, Math.Max(1, options.Value.Openverse.MaxRequestsPerMinute), TimeSpan.FromMinutes(1), _time);
    }

    public string Id => ProviderId;

    public string DisplayName => "Openverse";

    public string? LastRateLimitInfo => _rateLimit;

    public WallpaperProviderCapabilities Capabilities { get; } = new()
    {
        SupportsPopular = false,
        SupportsRandom = false,
        SupportsDetails = true,
        Sorts = [WallpaperSort.Relevance, WallpaperSort.Newest],
        SupportsAspectRatio = true,
        SupportsMinimumResolution = true,
        SupportsContentKind = true,
        SupportsLicenseFilter = true,
        SupportsApiKey = true,
        PageSize = PageSize,
        RateLimitSummary = "Anonymous: 20 requests per minute, 200 per day"
    };

    public async Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken = default)
    {
        var authenticated = HasCredentials();
        var depthLimit = authenticated ? int.MaxValue : AnonymousPaginationDepth;
        if (query.Page > 1 && (query.Page - 1) * PageSize >= depthLimit)
        {
            return WallpaperSearchResult.Empty(query.Page);
        }

        try
        {
            using var doc = await GetAsync("images/?" + BuildSearchQueryString(query), cancellationToken);
            return ParseSearch(doc.RootElement, query.Page, authenticated ? null : AnonymousPaginationDepth / PageSize);
        }
        catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.Unauthorized && query.Page > 1 && !authenticated)
        {
            // Openverse answers 401 once anonymous pagination goes too deep: that is simply the end of the results.
            return WallpaperSearchResult.Empty(query.Page) with { TotalPages = query.Page - 1 };
        }
    }

    public Task<WallpaperSearchResult> GetPopularAsync(int page, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Openverse does not provide a popular listing.");

    public Task<WallpaperSearchResult> GetRandomAsync(int page, string? seed, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Openverse does not provide random results.");

    public async Task<Wallpaper?> GetDetailsAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(externalId, out var id)) return null;
        try
        {
            using var doc = await GetAsync($"images/{id:D}/", cancellationToken);
            return MapWallpaper(doc.RootElement);
        }
        catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.NotFound)
        {
            return null;
        }
    }

    public static string BuildSearchQueryString(WallpaperSearchQuery query)
    {
        var parameters = new List<(string Key, string Value)>();
        var text = query.NormalizedText;
        if (text.Length > 0) parameters.Add(("q", text));

        parameters.Add(("page", Math.Max(1, query.Page).ToString(CultureInfo.InvariantCulture)));
        parameters.Add(("page_size", PageSize.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(("mature", "false"));

        var category = query.ContentKind switch
        {
            ContentKind.Photograph => "photograph",
            ContentKind.Illustration => "illustration",
            ContentKind.DigitizedArtwork => "digitized_artwork",
            _ => null
        };
        if (category is not null) parameters.Add(("category", category));

        var aspect = query.AspectRatio switch
        {
            AspectRatioFilter.Wide16x9 or AspectRatioFilter.Wide16x10 or AspectRatioFilter.UltraWide21x9 => "wide",
            AspectRatioFilter.Portrait => "tall",
            AspectRatioFilter.Square => "square",
            _ => null
        };
        if (aspect is not null) parameters.Add(("aspect_ratio", aspect));

        // Openverse only knows coarse size buckets; "large" is the closest match to "wallpaper sized".
        if (query.MinimumResolution != MinimumResolution.Any) parameters.Add(("size", "large"));

        var license = query.License switch
        {
            LicenseFilter.Commercial => "commercial",
            LicenseFilter.Modification => "modification",
            _ => null
        };
        if (license is not null) parameters.Add(("license_type", license));

        if (query.Sort == WallpaperSort.Newest) parameters.Add(("sort_by", "indexed_on"));

        return string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    public static WallpaperSearchResult ParseSearch(JsonElement root, int requestedPage, int? maxPages = null)
    {
        var items = new List<Wallpaper>();
        foreach (var element in root.GetArray("results"))
        {
            var mapped = MapWallpaper(element);
            if (mapped is not null) items.Add(mapped);
        }

        var pageCount = root.GetInt("page_count");
        if (pageCount is not null && maxPages is not null) pageCount = Math.Min(pageCount.Value, maxPages.Value);
        return new WallpaperSearchResult
        {
            Items = items,
            Page = root.GetInt("page") ?? requestedPage,
            TotalPages = pageCount,
            TotalItems = root.GetInt("result_count")
        };
    }

    public static Wallpaper? MapWallpaper(JsonElement e)
    {
        var id = e.GetString("id");
        var original = e.GetString("url");
        if (id is null) return null;

        var width = e.GetInt("width");
        var height = e.GetInt("height");
        var size = e.GetLong("filesize");
        var thumbnail = e.GetString("thumbnail");
        var fileType = NormalizeFileType(e.GetString("filetype"));

        // Show the original in the preview only when it is a modest download; otherwise stay with the thumbnail.
        var previewUrl = original is not null && size is > 0 && size <= MaxPreviewBytes && fileType is "image/jpeg" or "image/png" or "image/webp"
            ? original
            : thumbnail ?? original;

        var licenseCode = e.GetString("license");
        var licenseVersion = e.GetString("license_version");
        return new Wallpaper
        {
            ProviderId = ProviderId,
            ExternalId = id,
            Title = e.GetString("title"),
            ThumbnailUrl = thumbnail ?? original,
            PreviewUrl = previewUrl,
            FullUrl = original,
            SourcePageUrl = e.GetString("foreign_landing_url") ?? original,
            Resolution = width is > 0 && height is > 0 ? new WallpaperResolution(width.Value, height.Value) : null,
            FileSizeBytes = size,
            FileType = fileType,
            CreatedAt = DateTimeOffset.TryParse(e.GetString("indexed_on"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var indexed) ? indexed : null,
            Metadata = new WallpaperMetadata
            {
                CreatorName = e.GetString("creator"),
                CreatorUrl = e.GetString("creator_url"),
                LicenseName = FormatLicense(licenseCode, licenseVersion),
                LicenseUrl = e.GetString("license_url"),
                Attribution = e.GetString("attribution"),
                UpstreamSource = e.GetString("source") ?? e.GetString("provider"),
                Category = e.GetString("category"),
                Tags = e.GetArray("tags").Select(t => t.GetString("name")).Where(n => n is not null).Cast<string>().Take(24).ToList()
            }
        };
    }

    public static string? FormatLicense(string? code, string? version)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = code.Trim().ToLowerInvariant();
        return normalized switch
        {
            "cc0" => "CC0 " + (version ?? "1.0"),
            "pdm" => "Public Domain Mark " + (version ?? "1.0"),
            _ => $"CC {normalized.ToUpperInvariant()}" + (string.IsNullOrWhiteSpace(version) ? string.Empty : " " + version)
        };
    }

    private static string? NormalizeFileType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var t = type.Trim().ToLowerInvariant().TrimStart('.');
        return t switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            _ => t.Contains('/') ? t : "image/" + t
        };
    }

    private bool HasCredentials() =>
        !string.IsNullOrWhiteSpace(_settings.Current.OpenverseClientId) && !string.IsNullOrWhiteSpace(_settings.Current.OpenverseClientSecret);

    private async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Accept.ParseAdd("application/json");
        var token = await GetAccessTokenAsync(client, ct);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await ProviderHttp.GetJsonAsync(client, request, _gate, ProviderId, _logger, response =>
        {
            // Headers look like x-ratelimit-available-anon_burst: 19 / x-ratelimit-limit-anon_burst: 20/min
            var available = response.Headers.FirstOrDefault(h => h.Key.StartsWith("x-ratelimit-available-", StringComparison.OrdinalIgnoreCase));
            if (available.Key is not null)
            {
                var scope = available.Key["x-ratelimit-available-".Length..];
                var limit = response.Headers.TryGetValues("x-ratelimit-limit-" + scope, out var l) ? l.FirstOrDefault() : null;
                _rateLimit = limit is null ? $"{available.Value.FirstOrDefault()} requests left" : $"{available.Value.FirstOrDefault()} requests left (limit {limit})";
            }
        }, ct);
    }

    /// <summary>OAuth2 client-credentials flow documented by Openverse. Returns null for anonymous use.</summary>
    private async Task<string?> GetAccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        if (!HasCredentials()) return null;
        var clientId = _settings.Current.OpenverseClientId!.Trim();
        var secret = _settings.Current.OpenverseClientSecret!.Trim();
        var identity = clientId + "\n" + secret;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && _tokenFor == identity && _time.GetUtcNow() < _tokenExpiry) return _token;

            using var request = new HttpRequestMessage(HttpMethod.Post, "auth_tokens/token/")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = secret
                })
            };

            using var doc = await ProviderHttp.GetJsonAsync(client, request, _gate, ProviderId, _logger, null, ct);
            var token = doc.RootElement.GetString("access_token");
            if (token is null)
            {
                throw new ProviderException(ProviderId, ProviderErrorKind.Unauthorized, "Openverse did not accept the configured client credentials.");
            }

            var lifetime = doc.RootElement.GetLong("expires_in") ?? 3600;
            _token = token;
            _tokenFor = identity;
            _tokenExpiry = _time.GetUtcNow().AddSeconds(Math.Max(60, lifetime - 120));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
