using Luma.Core.Enums;

namespace Luma.Core.Models;

/// <summary>
/// Provider-neutral search request. Providers translate the fields they understand and ignore the rest
/// (the capabilities object tells the UI which fields are meaningful for which provider).
/// </summary>
public sealed record WallpaperSearchQuery
{
    public string? Text { get; init; }
    public int Page { get; init; } = 1;
    public WallpaperSort Sort { get; init; } = WallpaperSort.Relevance;
    public TopRange TopRange { get; init; } = TopRange.Month;
    public WallpaperCategories Categories { get; init; } = WallpaperCategories.All;
    public ContentPurity Purity { get; init; } = ContentPurity.Safe;
    public MinimumResolution MinimumResolution { get; init; } = MinimumResolution.Any;
    public AspectRatioFilter AspectRatio { get; init; } = AspectRatioFilter.Any;
    public IReadOnlyList<string> Colors { get; init; } = [];
    public ContentKind ContentKind { get; init; } = ContentKind.Any;
    public LicenseFilter License { get; init; } = LicenseFilter.Any;

    /// <summary>Seed used by providers to keep random result sets stable across pages.</summary>
    public string? Seed { get; init; }

    public string NormalizedText => (Text ?? string.Empty).Trim();

    public WallpaperSearchQuery NextPage() => this with { Page = Page + 1 };

    public WallpaperSearchQuery WithPage(int page) => this with { Page = Math.Max(1, page) };
}

public sealed record WallpaperSearchResult
{
    public required IReadOnlyList<Wallpaper> Items { get; init; }
    public int Page { get; init; } = 1;
    public int? TotalPages { get; init; }
    public int? TotalItems { get; init; }

    /// <summary>Seed to pass back for the next page of a random listing.</summary>
    public string? Seed { get; init; }

    public bool HasMore => TotalPages is { } total ? Page < total : Items.Count > 0;

    public static WallpaperSearchResult Empty(int page = 1) => new() { Items = [], Page = page, TotalPages = 0, TotalItems = 0 };
}

/// <summary>Honest description of what a provider can do so the UI can adapt itself.</summary>
public sealed record WallpaperProviderCapabilities
{
    public bool SupportsSearch { get; init; } = true;
    public bool SupportsPopular { get; init; }
    public bool SupportsRandom { get; init; }
    public bool SupportsDetails { get; init; }
    public IReadOnlyList<WallpaperSort> Sorts { get; init; } = [WallpaperSort.Relevance];
    public bool SupportsCategories { get; init; }
    public bool SupportsPurity { get; init; }
    public bool SupportsMinimumResolution { get; init; }
    public bool SupportsAspectRatio { get; init; }
    public bool SupportsColors { get; init; }
    public bool SupportsContentKind { get; init; }
    public bool SupportsLicenseFilter { get; init; }
    public bool SupportsApiKey { get; init; }
    public bool SupportsExplicitContent { get; init; }
    public int PageSize { get; init; } = 20;

    /// <summary>Human readable rate limit summary (e.g. "45 requests per minute").</summary>
    public string? RateLimitSummary { get; init; }
}

/// <summary>Observable status of a provider shown in the settings page.</summary>
public sealed record ProviderStatus(bool IsEnabled, bool IsHealthy, string Summary, string? RateLimit);
