using System.Net;
using Luma.Core.Interfaces;
using Luma.Infrastructure.Caching;
using Luma.Infrastructure.Database;
using Luma.Infrastructure.Downloads;
using Luma.Infrastructure.Platform;
using Luma.Infrastructure.Providers;
using Luma.Infrastructure.Services;
using Luma.Infrastructure.Platform.Windows.Video;
using Luma.Infrastructure.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Luma.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers every Luma service. Options must already be configured by the host.</summary>
    public static IServiceCollection AddLumaInfrastructure(this IServiceCollection services, LumaPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<ILumaPaths>(paths);
        services.AddOptions<LumaOptions>();

        services.AddSingleton<ISettingsService, SettingsService>();

        services.AddDbContextFactory<LumaDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath};Pooling=True"));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IFavoritesService, FavoritesService>();
        services.AddSingleton<ICollectionService, CollectionService>();
        services.AddSingleton<IDownloadLibrary, DownloadLibrary>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IStateStore, StateStore>();

        AddHttpClients(services);
        services.AddSingleton<IWallpaperProvider, WallhavenProvider>();
        services.AddSingleton<IWallpaperProvider, OpenverseProvider>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();

        services.AddSingleton<IImageFetcher, ImageFetcher>();
        services.AddSingleton<IDownloadService, DownloadService>();

        services.AddSingleton<IPlatformInfo, PlatformInfo>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<IWallpaperApplier>(sp => CreateApplier(sp));
        services.AddSingleton<IWallpaperApplyService, WallpaperApplyService>();
        services.AddSingleton<IRotationService, RotationService>();

        // Live (video) wallpapers: Windows gets the Media Foundation + desktop host implementation, other platforms an honest "unsupported".
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IDesktopVideoHost, WindowsDesktopVideoHost>();
            services.AddSingleton<IVideoProbe, WindowsVideoProbe>();
            services.AddSingleton<IVideoPreviewFactory, WindowsVideoPreviewFactory>();
        }
        else
        {
            services.AddSingleton(sp => new UnsupportedVideoPlatform(sp.GetRequiredService<IPlatformInfo>().Name));
            services.AddSingleton<IDesktopVideoHost>(sp => sp.GetRequiredService<UnsupportedVideoPlatform>());
            services.AddSingleton<IVideoProbe>(sp => sp.GetRequiredService<UnsupportedVideoPlatform>());
            services.AddSingleton<IVideoPreviewFactory>(sp => sp.GetRequiredService<UnsupportedVideoPlatform>());
        }
        services.AddSingleton<IVideoLibrary, VideoLibrary>();
        services.AddSingleton<IVideoWallpaperService, VideoWallpaperService>();
        return services;
    }

    private static void AddHttpClients(IServiceCollection services)
    {
        static SocketsHttpHandler CreateHandler() => new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 8
        };

        void Configure(HttpClient client, IServiceProvider sp, string? baseUrl, TimeSpan? timeout = null)
        {
            var options = sp.GetRequiredService<IOptions<LumaOptions>>().Value;
            if (baseUrl is not null) client.BaseAddress = new Uri(baseUrl);
            client.Timeout = timeout ?? TimeSpan.FromSeconds(Math.Clamp(options.ApiTimeoutSeconds, 5, 120));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        }

        services.AddHttpClient(WallhavenProvider.HttpClientName, (sp, c) => Configure(c, sp, sp.GetRequiredService<IOptions<LumaOptions>>().Value.Wallhaven.BaseUrl))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        services.AddHttpClient(OpenverseProvider.HttpClientName, (sp, c) => Configure(c, sp, sp.GetRequiredService<IOptions<LumaOptions>>().Value.Openverse.BaseUrl))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        services.AddHttpClient(ImageFetcher.HttpClientName, (sp, c) => Configure(c, sp, null, TimeSpan.FromSeconds(30)))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        // Full downloads can legitimately take a long time; DownloadService enforces its own inactivity timeout.
        services.AddHttpClient(DownloadService.HttpClientName, (sp, c) => Configure(c, sp, null, Timeout.InfiniteTimeSpan))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
    }

    private static IWallpaperApplier CreateApplier(IServiceProvider sp)
    {
        var platform = sp.GetRequiredService<IPlatformInfo>();
        if (OperatingSystem.IsWindows()) return ActivatorUtilities.CreateInstance<WindowsWallpaperApplier>(sp);
        if (platform.IsLinux) return ActivatorUtilities.CreateInstance<LinuxWallpaperApplier>(sp);
        return new UnsupportedWallpaperApplier(platform.Name);
    }
}
