using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Luma.App.Services;
using Luma.App.Services.Updates;
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
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private TrayIconController? _tray;
    private bool _quitting;

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
            _services.GetRequiredService<DialogService>();
            _services.GetRequiredService<MemoryDiagnostics>();
            _desktop = desktop;
            // Closing the window doesn't end Luma when it keeps running in the notification area; Quit does.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Shutdown();

            _tray = new TrayIconController(this, _services, ShowMainWindow, Quit);
            Program.Instance?.Listen(() => Dispatcher.UIThread.Post(ShowMainWindow));
            LaunchAtLogin.RefreshIfEnabled();

            // Started with Windows: stay in the notification area and just bring back the live wallpaper and rotation.
            // Without a tray icon there would be no way to reach Luma, so the window opens as usual then.
            var background = desktop.Args?.Contains(LaunchAtLogin.BackgroundArgument, StringComparer.OrdinalIgnoreCase) == true
                             && _tray.IsVisible;
            if (background)
            {
                Log.Information("Started in the background (launch at sign-in)");
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await shell.Startup.BeginAsync();
                    }
                    catch (Exception ex)
                    {
                        _services.GetRequiredService<ILogger<App>>().LogError(ex, "Background start-up failed");
                    }
                    StartUpdates(desktop);
                });
            }
            else
            {
                var window = CreateMainWindow();
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
                    StartUpdates(desktop);
                };
                desktop.MainWindow = window;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateMainWindow()
    {
        var services = _services ?? throw new InvalidOperationException("Services are not built yet.");
        var window = new MainWindow { DataContext = services.GetRequiredService<ShellViewModel>() };
        services.GetRequiredService<FileDialogService>().Owner = window;
        window.Closing += OnMainWindowClosing;
        window.Closed += OnMainWindowClosed;
        _window = window;
        return window;
    }

    /// <summary>Shows the window, creating a fresh one when it was closed to the notification area.</summary>
    public void ShowMainWindow()
    {
        if (_desktop is null || _services is null || _quitting) return;
        if (_window is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show();
            existing.Activate();
            return;
        }

        var window = CreateMainWindow();
        _desktop.MainWindow = window;
        window.Show();
        window.Activate();
        _ = ReopenAsync();

        async Task ReopenAsync()
        {
            try
            {
                await LumaServices.Get<ShellViewModel>().OnWindowReopenedAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reopening the window failed");
            }
        }
    }

    /// <summary>Ends Luma: the live wallpaper stops and its still frame stays on the desktop.</summary>
    public void Quit()
    {
        _quitting = true;
        _desktop?.Shutdown();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || _services is null) return;
        var keepRunning = _services.GetRequiredService<ISettingsService>().Current.CloseToTray && _tray?.IsVisible == true;
        if (!keepRunning) Quit();
    }

    /// <summary>
    /// The window is gone but Luma keeps running: release everything that only exists for the interface (page views,
    /// decoded thumbnails, the renderer's surfaces) so the background footprint is the services alone.
    /// </summary>
    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.Closing -= OnMainWindowClosing;
        window.Closed -= OnMainWindowClosed;
        if (ReferenceEquals(_window, window)) _window = null;
        if (_quitting || _services is null) return;

        _services.GetRequiredService<ShellViewModel>().OnWindowClosed();
        window.DataContext = null;
        _services.GetRequiredService<FileDialogService>().Owner = null;
        if (_desktop is not null && ReferenceEquals(_desktop.MainWindow, window)) _desktop.MainWindow = null;
        foreach (var locator in DataTemplates.OfType<PageViewLocator>()) locator.Clear();
        _services.GetRequiredService<ImageLoader>().Clear();

        // Give Avalonia a moment to tear down the window's visuals, then return the freed memory to Windows.
        DispatcherTimer.RunOnce(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Log.Information("Window closed; Luma keeps running in the notification area");
        }, TimeSpan.FromSeconds(2));
    }

    /// <summary>Background update checks begin only once the window is up; a failure here is logged and otherwise ignored.</summary>
    private void StartUpdates(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var updates = _services!.GetRequiredService<UpdateService>();
            // Velopack's updater waits for Luma to exit; a normal shutdown flushes settings and closes the database first.
            updates.RestartRequested += (_, _) => Dispatcher.UIThread.Post(Quit);
            updates.StartBackgroundChecks();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Automatic updates could not be started");
        }
    }

    private void Shutdown()
    {
        _quitting = true;
        _tray?.Dispose();
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
            // Some services (e.g. RotationService) are only IAsyncDisposable; synchronous Dispose() throws for them.
            try
            {
                if (_services is not null) Task.Run(() => _services.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while disposing services");
            }
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

    /// <summary>Forgets the cached views (their window was closed); the next window builds new ones.</summary>
    public void Clear() => _views.Clear();

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
            LiveWallpapersViewModel => new LiveWallpapersView(),
            HistoryViewModel => new HistoryView(),
            FeedbackViewModel => new FeedbackView(),
            SettingsViewModel => new SettingsView(),
            _ => null
        };
        if (view is null) return new TextBlock { Text = "Unknown page" };
        view.DataContext = param;
        _views[param] = view;
        return view;
    }
}
