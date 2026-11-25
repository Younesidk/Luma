using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace Luma.App.Services;

/// <summary>
/// Luma's icon in the taskbar's notification area. It stays for the whole life of the process, so the live wallpaper
/// and rotation keep running after the window is closed: clicking it reopens the window, and its menu can pause the
/// video or quit Luma. Must be created on the UI thread.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private const int MaxToolTipLength = 120; // Windows truncates tips at 127 characters

    private readonly IVideoWallpaperService _video;
    private readonly IUiDispatcher _dispatcher;
    private readonly TaskbarIcon? _icon;
    private readonly MenuFlyout _menu;
    private readonly MenuFlyoutItem _pauseItem;
    private bool _disposed;

    public TrayIconController(IServiceProvider services, Action showWindow, Action quit)
    {
        _video = services.GetRequiredService<IVideoWallpaperService>();
        _dispatcher = services.GetRequiredService<IUiDispatcher>();

        var open = new MenuFlyoutItem { Text = "Open Luma", Command = new RelayCommand(showWindow) };
        _pauseItem = new MenuFlyoutItem { Text = "Pause live wallpaper", Command = new RelayCommand(TogglePause) };
        var exit = new MenuFlyoutItem { Text = "Quit Luma", Command = new RelayCommand(quit) };
        // The pause item is inserted only while a live wallpaper runs (see Update).
        _menu = new MenuFlyout { Items = { open, new MenuFlyoutSeparator(), exit } };

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "luma.ico");
            _icon = new TaskbarIcon
            {
                ToolTipText = "Luma",
                Icon = new System.Drawing.Icon(iconPath),
                ContextMenuMode = ContextMenuMode.PopupMenu,
                ContextFlyout = _menu,
                LeftClickCommand = new RelayCommand(showWindow),
                NoLeftClickDelay = true
            };
            // Efficiency mode would throttle the live wallpaper and rotation while the window is closed.
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            // Without an icon there is no way back to a closed window, so closing then quits (see IsVisible).
            Log.Warning(ex, "The notification area icon couldn't be created");
            _icon?.Dispose();
            _icon = null;
        }

        _video.StatusChanged += OnVideoStatusChanged;
        Update(_video.Status);
    }

    /// <summary>True when the icon is in the notification area (so a closed window can be reopened from it).</summary>
    public bool IsVisible => !_disposed && _icon?.IsCreated == true;

    private void OnVideoStatusChanged(object? sender, VideoWallpaperStatus status) => _dispatcher.Post(() => Update(status));

    private void Update(VideoWallpaperStatus status)
    {
        if (_disposed) return;
        var running = status.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
        _pauseItem.Text = status.PauseReasons.HasFlag(PauseReasons.User) ? "Resume live wallpaper" : "Pause live wallpaper";
        var shown = _menu.Items.Contains(_pauseItem);
        if (running && !shown) _menu.Items.Insert(1, _pauseItem);
        else if (!running && shown) _menu.Items.Remove(_pauseItem);

        if (_icon is null) return;
        var text = status.Video is { } video && running
            ? $"Luma — {video.Title}{(status.State == VideoPlaybackState.Paused ? " (paused)" : string.Empty)}"
            : "Luma";
        _icon.ToolTipText = text.Length > MaxToolTipLength ? text[..(MaxToolTipLength - 1)] + "…" : text;
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
        if (_disposed) return;
        _disposed = true;
        _video.StatusChanged -= OnVideoStatusChanged;
        _icon?.Dispose();
    }
}
