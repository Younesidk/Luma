using System.Reflection;
using Luma.App.Services;
using Luma.App.Services.Updates;
using Luma.App.ViewModels;
using Luma.Core.Interfaces;
using Luma.Infrastructure;
using Luma.Infrastructure.Providers;
using Luma.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Luma.App;

/// <summary>Composition root: configuration, logging and the dependency injection container.</summary>
public static class LumaHost
{
    public static ServiceProvider Build()
    {
        var paths = new LumaPaths();
        paths.EnsureCreated();
        ConfigureLogging(paths);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("appsettings.json")
                           ?? throw new InvalidOperationException("appsettings.json is missing from the application.");
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .AddEnvironmentVariables("LUMA_")
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: true));
        services.Configure<LumaOptions>(configuration.GetSection(LumaOptions.SectionName));
        services.AddLumaInfrastructure(paths);
        services.Configure<LumaUpdateOptions>(configuration.GetSection(LumaUpdateOptions.SectionName));
        services.Configure<LumaFeedbackOptions>(configuration.GetSection(LumaFeedbackOptions.SectionName));
        AddApplication(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
    }

    private static void AddApplication(IServiceCollection services)
    {
        services.AddSingleton<ImageLoader>();
        services.AddSingleton<IImageTranscoder, ImageTranscoder>();
        services.AddSingleton<MemoryDiagnostics>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<PreviewService>();
        services.AddSingleton<IPreviewService>(sp => sp.GetRequiredService<PreviewService>());
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<FileDialogService>();
        services.AddSingleton<IFileDialogService>(sp => sp.GetRequiredService<FileDialogService>());
        services.AddSingleton<LibraryEventsBridge>();
        services.AddSingleton<WallpaperActions>();
        services.AddSingleton<WallpaperItemFactory>();
        services.AddSingleton<ShellStartup>();
        services.AddSingleton<IUpdateClient, VelopackUpdateClient>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<UpdateService>());
        services.AddSingleton<UpdateViewModel>();
        services.AddHttpClient(FeedbackService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<FeedbackService>();

        services.AddSingleton<DiscoverViewModel>();
        services.AddSingleton<FavoritesViewModel>();
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<LiveWallpapersViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<FeedbackViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    private static void ConfigureLogging(LumaPaths paths)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "luma-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}

