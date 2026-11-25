using Luma.App.Services;
using Luma.App.Views;
using Luma.App.Services.Updates;
using Luma.App.ViewModels;
using Luma.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;

namespace Luma.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private UiDispatcher? _dispatcher;
    private MainWindow? _window;
    private TrayIconController? _tray;
    private bool _quitting;
    private bool _shutDown;

    public App()
    {
        InitializeComponent();
        // Closing the window does not end Luma when it keeps running in the notification area; Quit does.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _services = LumaHost.Build();
        LumaServices.Initialize(_services);
        _dispatcher = _services.GetRequiredService<UiDispatcher>();
        _dispatcher.Attach(DispatcherQueue.GetForCurrentThread());
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

        _tray = new TrayIconController(_services, ShowMainWindow, Quit);
        Program.Instance?.Listen(() => _dispatcher.Post(ShowMainWindow));

        // Started with Windows: stay in the notification area and just bring back the live wallpaper and rotation.
        // Without a tray icon there would be no way to reach Luma, so the window opens as usual then.
        if (Program.StartInBackground && _tray.IsVisible)
        {
            Log.Information("Started in the background (launch at sign-in)");
            _dispatcher.Post(async () =>
            {
                try
                {
                    await shell.Startup.BeginAsync();
                }
                catch (Exception ex)
                {
                    _services.GetRequiredService<ILogger<App>>().LogError(ex, "Background start-up failed");
                }
                StartUpdates();
            });
            return;
        }

        var window = CreateMainWindow();
        window.Activate();
        _dispatcher.Post(async () =>
        {
            try
            {
                await shell.StartAsync();
            }
            catch (Exception ex)
            {
                _services.GetRequiredService<ILogger<App>>().LogError(ex, "Start-up failed");
            }
            StartUpdates();
        });
    }

    private MainWindow CreateMainWindow()
    {
        var services = _services ?? throw new InvalidOperationException("Services are not built yet.");
        var window = new MainWindow(services.GetRequiredService<ShellViewModel>());
        services.GetRequiredService<FileDialogService>().Owner = window;
        services.GetRequiredService<ThemeService>().Attach(window.RootElement);
        window.Closed += OnMainWindowClosed;
        _window = window;
        return window;
    }

    /// <summary>Shows the window, creating a fresh one when it was closed to the notification area.</summary>
    public void ShowMainWindow()
    {
        if (_services is null || _quitting) return;
        if (_window is { } existing)
        {
            if (existing.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
            existing.AppWindow.Show();
            existing.Activate();
            return;
        }

        var window = CreateMainWindow();
        window.Activate();
        _ = ReopenAsync();

        static async Task ReopenAsync()
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
        if (_quitting) return;
        _quitting = true;
        _window?.Close();
        Shutdown();
        Exit();
    }

    /// <summary>
    /// The window closed. When Luma keeps running in the notification area, release everything that only exists for the
    /// interface (views, decoded thumbnails, composition surfaces) so the background footprint is the services alone;
    /// otherwise this ends Luma.
    /// </summary>
    private void OnMainWindowClosed(object sender, WindowEventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.Closed -= OnMainWindowClosed;
        if (ReferenceEquals(_window, window)) _window = null;
        if (_quitting || _services is null) return;

        var keepRunning = _services.GetRequiredService<ISettingsService>().Current.CloseToTray && _tray?.IsVisible == true;
        if (!keepRunning)
        {
            Quit();
            return;
        }

        _services.GetRequiredService<ShellViewModel>().OnWindowClosed();
        _services.GetRequiredService<FileDialogService>().Owner = null;
        _services.GetRequiredService<ThemeService>().Attach(null);
        _services.GetRequiredService<ImageLoader>().Clear();

        // Give XAML a moment to tear down the window's visuals, then return the freed memory to Windows.
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Log.Information("Window closed; Luma keeps running in the notification area");
        };
        timer.Start();
    }

    /// <summary>Background update checks begin only once the app is up; a failure here is logged and otherwise ignored.</summary>
    private void StartUpdates()
    {
        try
        {
            var updates = _services!.GetRequiredService<UpdateService>();
            // Velopack's updater waits for Luma to exit; a normal shutdown flushes settings and closes the database first.
            updates.RestartRequested += (_, _) => _dispatcher!.Post(Quit);
            updates.StartBackgroundChecks();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Automatic updates could not be started");
        }
    }

    private void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
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
        UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            _services?.GetService<IToastService>()?.Show("Something went wrong. Details were written to the log.", ToastKind.Error);
            e.Handled = true;
        };
    }
}
