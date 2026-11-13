using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Luma.App.Services;
using Luma.App.ViewModels;
using Luma.App.Views;
using Luma.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Luma.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = LumaHost.Build();
            LumaServices.Initialize(_services);
            HookGlobalExceptionHandlers();

            // Settings are tiny; reading them before the window exists avoids a theme flash on start-up.
            var settings = _services.GetRequiredService<ISettingsService>();
            // Run on the thread pool so blocking here can never deadlock against the UI synchronisation context.
            Task.Run(() => settings.LoadAsync()).GetAwaiter().GetResult();
            var theme = _services.GetRequiredService<ThemeService>();
            theme.Apply(settings.Current.Theme, settings.Current.AccentPreset);

            var shell = _services.GetRequiredService<ShellViewModel>();
            var window = new MainWindow { DataContext = shell };
            _services.GetRequiredService<FileDialogService>().Owner = window;
            _services.GetRequiredService<DialogService>();
            _services.GetRequiredService<MemoryDiagnostics>();
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => Shutdown();
            window.Opened += async (_, _) =>
            {
                try
                {
                    await shell.StartAsync();
                }
                catch (Exception ex)
                {
                    _services.GetRequiredService<ILogger<App>>().LogError(ex, "Start-up failed");
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        try
        {
            if (_services is null) return;
            var rotation = _services.GetRequiredService<IRotationService>();
            Task.Run(rotation.StopAsync).GetAwaiter().GetResult();
            Task.Run(() => _services.GetRequiredService<ISettingsService>().FlushAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while shutting down");
        }
        finally
        {
            _services?.Dispose();
            Log.CloseAndFlush();
        }
    }

    private void HookGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            _services?.GetService<IToastService>()?.Show("Something went wrong. Details were written to the log.", ToastKind.Error);
            e.Handled = true;
        };
    }
}

/// <summary>Maps page view models to their views and keeps each view alive, so scroll position and focus survive navigation.</summary>
public sealed class PageViewLocator : IDataTemplate
{
    private readonly Dictionary<object, Control> _views = new(ReferenceEqualityComparer.Instance);

    public bool Match(object? data) => data is PageViewModel;

    public Control? Build(object? param)
    {
        if (param is null) return null;
        if (_views.TryGetValue(param, out var cached)) return cached;

        Control? view = param switch
        {
            DiscoverViewModel => new DiscoverView(),
            FavoritesViewModel => new FavoritesView(),
            CollectionsViewModel => new CollectionsView(),
            DownloadsViewModel => new DownloadsView(),
            HistoryViewModel => new HistoryView(),
            SettingsViewModel => new SettingsView(),
            _ => null
        };
        if (view is null) return new TextBlock { Text = "Unknown page" };
        view.DataContext = param;
        _views[param] = view;
        return view;
    }
}
