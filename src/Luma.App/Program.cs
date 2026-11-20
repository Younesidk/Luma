using System.Text.Json;
using Avalonia;
using Avalonia.Win32;
using Avalonia.X11;
using Luma.App.Services;
using Luma.Infrastructure.Services;
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
        if (OperatingSystem.IsWindows()) velopack = velopack.OnBeforeUninstallFastCallback(_ => LaunchAtLogin.RemoveQuietly());
        velopack.Run();

        // Luma lives on in the notification area after its window closes; a second launch just shows that window.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return;
        Instance = instance;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The single-instance guard of this process (the app listens on it for "show the window" requests).</summary>
    internal static SingleInstance? Instance { get; private set; }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // GPU rendering is the default for smooth animation; the opt-in CPU rasteriser trades that for a much lower memory footprint.
        if (UseSoftwareRendering())
        {
            if (OperatingSystem.IsWindows())
                builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
            else if (OperatingSystem.IsLinux())
                builder = builder.With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
        }
        return builder;
    }

    /// <summary>
    /// Decides the renderer before any window exists. LUMA_RENDERING=software|accelerated overrides the saved setting
    /// ("RenderingMode" in settings.json); the default is GPU rendering. Mirrors <see cref="Luma.Core.Models.AppSettings.Migrate"/>:
    /// a settings file from before versioning is treated as accelerated, because the old "Efficient" value there was only the old default.
    /// </summary>
    private static bool UseSoftwareRendering()
    {
        var environment = Environment.GetEnvironmentVariable("LUMA_RENDERING");
        if (!string.IsNullOrWhiteSpace(environment)) return !environment.Equals("accelerated", StringComparison.OrdinalIgnoreCase);

        try
        {
            var path = new LumaPaths().SettingsPath;
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var version = root.TryGetProperty("SettingsVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            if (version < 1) return false;
            if (root.TryGetProperty("RenderingMode", out var value) && value.ValueKind == JsonValueKind.String)
                return string.Equals(value.GetString(), "Efficient", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable settings file must never prevent start-up; fall through to the default.
        }
        return false;
    }
}
