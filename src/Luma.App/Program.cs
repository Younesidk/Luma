using Luma.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Luma.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: Velopack handles its install/update/uninstall hooks here (and exits the process for them),
        // and applies an update that was downloaded in a previous session. In a plain development build it does nothing.
        var velopack = VelopackApp.Build().SetArgs(args);
        // Uninstalling must not leave a Run entry pointing at a deleted exe.
        velopack = velopack.OnBeforeUninstallFastCallback(_ => LaunchAtLogin.RemoveQuietly());
        velopack.Run();

        // Luma lives on in the notification area after its window closes; a second launch just shows that window.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return;
        Instance = instance;

        // Started with Windows: stay in the notification area (App decides, once it knows the tray icon exists).
        StartInBackground = args.Contains(LaunchAtLogin.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        LaunchAtLogin.RefreshIfEnabled();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    /// <summary>The single-instance guard of this process (the app listens on it for "show the window" requests).</summary>
    internal static SingleInstance? Instance { get; private set; }

    /// <summary>True when launched with <see cref="LaunchAtLogin.BackgroundArgument"/> (start-up at Windows sign-in).</summary>
    internal static bool StartInBackground { get; private set; }
}
