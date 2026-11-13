using Luma.Core.Interfaces;

namespace Luma.Infrastructure.Providers;

/// <summary>Holds every registered provider and applies the user's enable/disable switches from the settings.</summary>
public sealed class ProviderRegistry(IEnumerable<IWallpaperProvider> providers, ISettingsService settings) : IProviderRegistry
{
    private readonly IReadOnlyList<IWallpaperProvider> _providers = providers.ToList();

    public IReadOnlyList<IWallpaperProvider> All => _providers;

    public IEnumerable<IWallpaperProvider> Enabled => _providers.Where(p => IsEnabled(p.Id));

    public IWallpaperProvider? Get(string providerId) =>
        _providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled(string providerId) => providerId switch
    {
        WallhavenProvider.ProviderId => settings.Current.WallhavenEnabled,
        OpenverseProvider.ProviderId => settings.Current.OpenverseEnabled,
        _ => true
    };

    public string GetDisplayName(string providerId) =>
        Get(providerId)?.DisplayName ?? (providerId == Core.Models.Wallpaper.LocalProviderId ? "Local" : providerId);
}
