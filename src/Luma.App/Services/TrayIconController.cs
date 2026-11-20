using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Luma.App.Services;

/// <summary>
/// Luma's icon in the taskbar's notification area. It stays for the whole life of the process, so the live wallpaper
/// and rotation keep running after the window is closed: clicking it reopens the window, and its menu can pause the
/// video or quit Luma.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly IVideoWallpaperService _video;
    private readonly TrayIcon? _icon;
    private readonly NativeMenuItem _pauseItem;

    public TrayIconController(Application app, IServiceProvider services, Action showWindow, Action quit)
    {
        _video = services.GetRequiredService<IVideoWallpaperService>();

        var open = new NativeMenuItem("Open Luma");
        open.Click += (_, _) => showWindow();
        _pauseItem = new NativeMenuItem("Pause live wallpaper") { IsVisible = false };
        _pauseItem.Click += (_, _) => TogglePause();
        var exit = new NativeMenuItem("Quit Luma");
        exit.Click += (_, _) => quit();

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Luma/Assets/luma.ico"));
            _icon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "Luma",
                Menu = new NativeMenu { Items = { open, _pauseItem, new NativeMenuItemSeparator(), exit } },
                IsVisible = true
            };
            _icon.Clicked += (_, _) => showWindow();
            TrayIcon.SetIcons(app, [_icon]);
        }
        catch (Exception ex)
        {
            // Without an icon there is no way back to a closed window, so closing then quits (see IsVisible).
            Log.Warning(ex, "The notification area icon couldn't be created");
            _icon = null;
        }

        _video.StatusChanged += OnVideoStatusChanged;
        Update(_video.Status);
    }

    /// <summary>True when the icon is in the notification area (so a closed window can be reopened from it).</summary>
    public bool IsVisible => _icon?.IsVisible == true;

    private void OnVideoStatusChanged(object? sender, VideoWallpaperStatus status) => Dispatcher.UIThread.Post(() => Update(status));

    private void Update(VideoWallpaperStatus status)
    {
        var running = status.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
        _pauseItem.IsVisible = running;
        _pauseItem.Header = status.PauseReasons.HasFlag(PauseReasons.User) ? "Resume live wallpaper" : "Pause live wallpaper";
        if (_icon is null) return;
        var text = status.Video is { } video && running
            ? $"Luma — {video.Title}{(status.State == VideoPlaybackState.Paused ? " (paused)" : string.Empty)}"
            : "Luma";
        _icon.ToolTipText = text.Length > 120 ? text[..119] + "…" : text; // Windows truncates tips at 127 characters
    }

    private async void TogglePause()
    {
        try
        {
            await _video.SetUserPausedAsync(!_video.Status.PauseReasons.HasFlag(PauseReasons.User));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pausing the live wallpaper from the notification area failed");
        }
    }

    public void Dispose()
    {
        _video.StatusChanged -= OnVideoStatusChanged;
        if (_icon is null) return;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
