using System.Text.Json;
using Avalonia;
using Avalonia.Win32;
using Avalonia.X11;
using Luma.Infrastructure.Services;

namespace Luma.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // CPU rasterisation keeps Luma's memory footprint far lower than GPU-backed rendering (no driver textures).
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
    /// ("RenderingMode" in settings.json); the default is the efficient software renderer.
    /// </summary>
    private static bool UseSoftwareRendering()
    {
        var environment = Environment.GetEnvironmentVariable("LUMA_RENDERING");
        if (!string.IsNullOrWhiteSpace(environment)) return !environment.Equals("accelerated", StringComparison.OrdinalIgnoreCase);

        try
        {
            var path = new LumaPaths().SettingsPath;
            if (!File.Exists(path)) return true;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.TryGetProperty("RenderingMode", out var value) && value.ValueKind == JsonValueKind.String)
                return !string.Equals(value.GetString(), "Accelerated", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable settings file must never prevent start-up; fall through to the default.
        }
        return true;
    }
}
