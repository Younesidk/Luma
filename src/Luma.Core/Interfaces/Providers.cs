using Luma.Core.Models;

namespace Luma.Core.Interfaces;

/// <summary>A source of wallpapers (Wallhaven, Openverse, ...). Implementations must be thread-safe.</summary>
public interface IWallpaperProvider
{
    /// <summary>Stable lower-case identifier, stored in the database.</summary>
    string Id { get; }

    string DisplayName { get; }

    WallpaperProviderCapabilities Capabilities { get; }

    /// <summary>Most recent rate limit information reported by the provider, if any.</summary>
    string? LastRateLimitInfo { get; }

    Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Popular / top rated wallpapers. Only valid when <see cref="WallpaperProviderCapabilities.SupportsPopular"/>.</summary>
    Task<WallpaperSearchResult> GetPopularAsync(int page, CancellationToken cancellationToken = default);

    /// <summary>Random wallpapers. Only valid when <see cref="WallpaperProviderCapabilities.SupportsRandom"/>.</summary>
    Task<WallpaperSearchResult> GetRandomAsync(int page, string? seed, CancellationToken cancellationToken = default);

    /// <summary>Full details for one wallpaper. Providers without a detail endpoint return null.</summary>
    Task<Wallpaper?> GetDetailsAsync(string externalId, CancellationToken cancellationToken = default);
}

/// <summary>Knows every registered provider and whether the user enabled it.</summary>
public interface IProviderRegistry
{
    IReadOnlyList<IWallpaperProvider> All { get; }

    IEnumerable<IWallpaperProvider> Enabled { get; }

    IWallpaperProvider? Get(string providerId);

    bool IsEnabled(string providerId);

    string GetDisplayName(string providerId);
}
