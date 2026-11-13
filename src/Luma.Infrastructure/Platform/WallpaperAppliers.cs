using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Luma.Infrastructure.Platform;

/// <summary>Validation shared by every platform adapter. Wallpaper metadata is never used to build commands.</summary>
public static class WallpaperPathValidator
{
    public static readonly string[] NativeExtensions = [".jpg", ".jpeg", ".png", ".bmp"];
    public static readonly string[] KnownImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    /// <summary>Returns null when the path is acceptable, otherwise a user-facing reason.</summary>
    public static ApplyResult? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            return ApplyResult.Fail(ApplyFailureReason.InvalidPath, "That file path isn't valid.");
        if (!Path.IsPathFullyQualified(path))
            return ApplyResult.Fail(ApplyFailureReason.InvalidPath, "Wallpapers must be applied from a full file path.");
        if (!File.Exists(path))
            return ApplyResult.Fail(ApplyFailureReason.FileNotFound, "The wallpaper file no longer exists. It may have been moved or deleted.");
        if (!KnownImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return ApplyResult.Fail(ApplyFailureReason.InvalidPath, "That file isn't a supported image type.");
        return null;
    }

    public static bool IsNativelySupported(string path) =>
        NativeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Applies wallpapers on Windows through SystemParametersInfo and the documented registry style values.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsWallpaperApplier(ILogger<WindowsWallpaperApplier> logger) : IWallpaperApplier
{
    private const uint SpiSetDeskWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    public WallpaperApplierCapabilities Capabilities { get; } = new(
        true, "Windows",
        [WallpaperFitMode.Fill, WallpaperFitMode.Fit, WallpaperFitMode.Stretch, WallpaperFitMode.Tile, WallpaperFitMode.Center, WallpaperFitMode.Span],
        "Applied to every monitor. Span stretches one image across all monitors.");

    public async Task<ApplyResult> ApplyAsync(string filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default)
    {
        if (WallpaperPathValidator.Validate(filePath) is { } invalid) return invalid;
        if (!WallpaperPathValidator.IsNativelySupported(filePath))
            return ApplyResult.Fail(ApplyFailureReason.InvalidPath, "Windows can only use JPEG, PNG or BMP files as wallpaper.");

        var work = Task.Run(() => ApplyCore(filePath, fitMode), cancellationToken);
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20), cancellationToken));
        if (finished != work)
        {
            return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, "Windows didn't respond in time. The wallpaper may still change in a moment.");
        }
        return await work;
    }

    private ApplyResult ApplyCore(string filePath, WallpaperFitMode fitMode)
    {
        try
        {
            SetStyle(fitMode);
            if (!SystemParametersInfo(SpiSetDeskWallpaper, 0, filePath, SpifUpdateIniFile | SpifSendChange))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                logger.LogWarning("SystemParametersInfo rejected the wallpaper: {Message} ({Code})", error.Message, error.NativeErrorCode);
                return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, $"Windows rejected the wallpaper: {error.Message}");
            }
            return ApplyResult.Ok();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogError(ex, "Could not update desktop wallpaper settings");
            return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, "Luma isn't allowed to change the desktop wallpaper settings.");
        }
    }

    private static void SetStyle(WallpaperFitMode mode)
    {
        var (style, tile) = mode switch
        {
            WallpaperFitMode.Fill => ("10", "0"),
            WallpaperFitMode.Fit => ("6", "0"),
            WallpaperFitMode.Stretch => ("2", "0"),
            WallpaperFitMode.Tile => ("0", "1"),
            WallpaperFitMode.Center => ("0", "0"),
            WallpaperFitMode.Span => ("22", "0"),
            _ => ("10", "0")
        };

        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true)
                        ?? throw new IOException("The desktop settings key could not be opened.");
        key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
        key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, string pvParam, uint winIni);
}

/// <summary>
/// Linux adapter. Detects the desktop environment and drives its native tooling (gsettings, plasma-apply-wallpaperimage,
/// xfconf-query, swaymsg, feh). Everything is executed without a shell and paths are validated first.
/// </summary>
public sealed class LinuxWallpaperApplier : IWallpaperApplier
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private readonly IPlatformInfo _platform;
    private readonly IProcessRunner _runner;
    private readonly ILogger<LinuxWallpaperApplier> _logger;

    public LinuxWallpaperApplier(IPlatformInfo platform, IProcessRunner runner, ILogger<LinuxWallpaperApplier> logger)
    {
        _platform = platform;
        _runner = runner;
        _logger = logger;
        Backend = DetectBackend(platform);
        Capabilities = DescribeBackend(Backend, platform);
    }

    public enum LinuxBackend { None, Gnome, Cinnamon, Mate, Kde, Xfce, Sway, Feh }

    public LinuxBackend Backend { get; }

    public WallpaperApplierCapabilities Capabilities { get; }

    internal static LinuxBackend DetectBackend(IPlatformInfo platform)
    {
        var desktop = platform.DesktopEnvironment;
        bool Has(params string[] names) => names.Any(n => desktop.Contains(n, StringComparison.OrdinalIgnoreCase));

        if (Has("cinnamon")) return LinuxBackend.Cinnamon;
        if (Has("mate")) return LinuxBackend.Mate;
        if (Has("gnome", "unity", "budgie", "pantheon", "deepin", "ubuntu", "pop")) return LinuxBackend.Gnome;
        if (Has("kde", "plasma")) return LinuxBackend.Kde;
        if (Has("xfce")) return LinuxBackend.Xfce;
        if (Has("sway")) return LinuxBackend.Sway;
        return platform.SessionType == "x11" ? LinuxBackend.Feh : LinuxBackend.None;
    }

    private static WallpaperApplierCapabilities DescribeBackend(LinuxBackend backend, IPlatformInfo platform)
    {
        WallpaperFitMode[] all = [WallpaperFitMode.Fill, WallpaperFitMode.Fit, WallpaperFitMode.Stretch, WallpaperFitMode.Tile, WallpaperFitMode.Center, WallpaperFitMode.Span];
        WallpaperFitMode[] noSpan = [WallpaperFitMode.Fill, WallpaperFitMode.Fit, WallpaperFitMode.Stretch, WallpaperFitMode.Tile, WallpaperFitMode.Center];
        var name = string.IsNullOrEmpty(platform.DesktopEnvironment) ? "Linux" : $"Linux ({platform.DesktopEnvironment})";
        return backend switch
        {
            LinuxBackend.Gnome => new(true, name, all, "Uses gsettings (GNOME schema), including the dark-mode wallpaper."),
            LinuxBackend.Cinnamon => new(true, name, all, "Uses gsettings (Cinnamon schema)."),
            LinuxBackend.Mate => new(true, name, all, "Uses gsettings (MATE schema)."),
            LinuxBackend.Kde => new(true, name, [WallpaperFitMode.Fill], "KDE Plasma applies wallpapers with plasma-apply-wallpaperimage, which has no fit options."),
            LinuxBackend.Xfce => new(true, name, noSpan, "Uses xfconf-query on every monitor/workspace."),
            LinuxBackend.Sway => new(true, name, noSpan, "Uses swaymsg; the wallpaper lasts until Sway restarts (add it to your Sway config to keep it)."),
            LinuxBackend.Feh => new(true, name, noSpan, "Uses feh on X11; the wallpaper lasts for the current session."),
            _ => new(false, name, [], platform.SessionType == "wayland"
                ? "This Wayland compositor has no standard wallpaper API Luma can drive. GNOME, KDE Plasma, Cinnamon, MATE, XFCE and Sway are supported."
                : "Luma could not detect a supported desktop environment (GNOME, KDE Plasma, Cinnamon, MATE, XFCE, Sway or an X11 session with feh).")
        };
    }

    public async Task<ApplyResult> ApplyAsync(string filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsSupported)
            return ApplyResult.Fail(ApplyFailureReason.UnsupportedPlatform, Capabilities.Notes ?? "This desktop isn't supported.");
        if (WallpaperPathValidator.Validate(filePath) is { } invalid) return invalid;
        if (!Capabilities.SupportedFitModes.Contains(fitMode))
            return ApplyResult.Fail(ApplyFailureReason.UnsupportedFitMode, $"This desktop can't use the \"{fitMode}\" mode.");

        try
        {
            switch (Backend)
            {
                case LinuxBackend.Xfce:
                    return await ApplyXfceAsync(filePath, fitMode, cancellationToken);
                default:
                    foreach (var (file, args) in BuildCommands(Backend, filePath, fitMode))
                    {
                        var result = await _runner.RunAsync(file, args, CommandTimeout, cancellationToken);
                        if (result.CommandNotFound)
                            return ApplyResult.Fail(ApplyFailureReason.CommandNotFound, $"\"{file}\" isn't installed, so Luma can't change the wallpaper on this desktop.");
                        if (!result.Succeeded)
                        {
                            _logger.LogWarning("{Command} failed with exit code {Code}: {Error}", file, result.ExitCode, result.StandardError.Trim());
                            return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, $"The desktop refused the wallpaper ({file} exited with code {result.ExitCode}).");
                        }
                    }
                    return ApplyResult.Ok();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying wallpaper on Linux failed");
            return ApplyResult.Fail(ApplyFailureReason.Unknown, "Something went wrong while changing the wallpaper. Details were written to the log.");
        }
    }

    internal static IReadOnlyList<(string File, string[] Args)> BuildCommands(LinuxBackend backend, string path, WallpaperFitMode fit)
    {
        var uri = new Uri(path).AbsoluteUri;
        switch (backend)
        {
            case LinuxBackend.Gnome:
                return
                [
                    ("gsettings", ["set", "org.gnome.desktop.background", "picture-options", GsettingsOption(fit)]),
                    ("gsettings", ["set", "org.gnome.desktop.background", "picture-uri", uri]),
                    ("gsettings", ["set", "org.gnome.desktop.background", "picture-uri-dark", uri])
                ];
            case LinuxBackend.Cinnamon:
                return
                [
                    ("gsettings", ["set", "org.cinnamon.desktop.background", "picture-options", GsettingsOption(fit)]),
                    ("gsettings", ["set", "org.cinnamon.desktop.background", "picture-uri", uri])
                ];
            case LinuxBackend.Mate:
                return
                [
                    ("gsettings", ["set", "org.mate.background", "picture-options", GsettingsOption(fit)]),
                    ("gsettings", ["set", "org.mate.background", "picture-filename", path])
                ];
            case LinuxBackend.Kde:
                return [("plasma-apply-wallpaperimage", [path])];
            case LinuxBackend.Sway:
                var mode = fit switch
                {
                    WallpaperFitMode.Fit => "fit",
                    WallpaperFitMode.Stretch => "stretch",
                    WallpaperFitMode.Tile => "tile",
                    WallpaperFitMode.Center => "center",
                    _ => "fill"
                };
                return [("swaymsg", ["output", "*", "bg", path, mode])];
            case LinuxBackend.Feh:
                var flag = fit switch
                {
                    WallpaperFitMode.Fit => "--bg-max",
                    WallpaperFitMode.Stretch => "--bg-scale",
                    WallpaperFitMode.Tile => "--bg-tile",
                    WallpaperFitMode.Center => "--bg-center",
                    _ => "--bg-fill"
                };
                return [("feh", [flag, path])];
            default:
                return [];
        }
    }

    private static string GsettingsOption(WallpaperFitMode fit) => fit switch
    {
        WallpaperFitMode.Fill => "zoom",
        WallpaperFitMode.Fit => "scaled",
        WallpaperFitMode.Stretch => "stretched",
        WallpaperFitMode.Tile => "wallpaper",
        WallpaperFitMode.Center => "centered",
        WallpaperFitMode.Span => "spanned",
        _ => "zoom"
    };

    private async Task<ApplyResult> ApplyXfceAsync(string path, WallpaperFitMode fit, CancellationToken ct)
    {
        var list = await _runner.RunAsync("xfconf-query", ["-c", "xfce4-desktop", "-l"], CommandTimeout, ct);
        if (list.CommandNotFound)
            return ApplyResult.Fail(ApplyFailureReason.CommandNotFound, "\"xfconf-query\" isn't installed, so Luma can't change the wallpaper on XFCE.");
        if (!list.Succeeded)
            return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, "XFCE didn't answer the wallpaper query.");

        var style = fit switch
        {
            WallpaperFitMode.Center => "1",
            WallpaperFitMode.Tile => "2",
            WallpaperFitMode.Stretch => "3",
            WallpaperFitMode.Fit => "4",
            _ => "5"
        };

        var properties = list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith("/last-image", StringComparison.Ordinal))
            .ToList();
        if (properties.Count == 0)
            return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, "XFCE has no desktop wallpaper properties to change.");

        foreach (var property in properties)
        {
            var styleProperty = property[..^"last-image".Length] + "image-style";
            var setImage = await _runner.RunAsync("xfconf-query", ["-c", "xfce4-desktop", "-p", property, "-s", path], CommandTimeout, ct);
            var setStyle = await _runner.RunAsync("xfconf-query", ["-c", "xfce4-desktop", "-p", styleProperty, "-s", style], CommandTimeout, ct);
            if (!setImage.Succeeded || !setStyle.Succeeded)
                return ApplyResult.Fail(ApplyFailureReason.OperatingSystemRejected, "XFCE refused the wallpaper change.");
        }
        return ApplyResult.Ok();
    }
}

/// <summary>Used where no real implementation exists (macOS today). It reports honestly instead of pretending.</summary>
public sealed class UnsupportedWallpaperApplier(string platformName) : IWallpaperApplier
{
    public WallpaperApplierCapabilities Capabilities { get; } = new(
        false, platformName, [], $"Setting the desktop wallpaper isn't supported on {platformName} yet. You can still discover, download and organise wallpapers.");

    public Task<ApplyResult> ApplyAsync(string filePath, WallpaperFitMode fitMode, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApplyResult.Fail(ApplyFailureReason.UnsupportedPlatform, Capabilities.Notes!));
}
