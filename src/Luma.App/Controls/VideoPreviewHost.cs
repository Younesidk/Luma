using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Luma.App.Services;
using Luma.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luma.App.Controls;

/// <summary>
/// Plays a video inside Luma's window (muted, looping, letterboxed). The picture is drawn by the platform video engine
/// into a native child window, so Avalonia content can't be drawn on top of it: keep overlays outside its bounds.
/// A control instance plays one <see cref="Source"/>; bind a new instance (e.g. through a data template) to change videos.
/// </summary>
public sealed class VideoPreviewHost : NativeControlHost
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<VideoPreviewHost, string?>(nameof(Source));

    public static readonly StyledProperty<string?> ErrorProperty =
        AvaloniaProperty.Register<VideoPreviewHost, string?>(nameof(Error), defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    private IVideoPreviewSurface? _surface;

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Set by the control when the video can't be played.</summary>
    public string? Error { get => GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var factory = LumaServices.Get<IVideoPreviewFactory>();
        if (Source is { Length: > 0 } path && factory.IsSupported && parent.HandleDescriptor == "HWND")
        {
            try
            {
                var surface = factory.Create(parent.Handle, path);
                surface.Failed += (_, message) => Dispatcher.UIThread.Post(() => Error = message);
                _surface = surface;
                return new PlatformHandle(surface.Handle, "HWND");
            }
            catch (Exception ex)
            {
                LumaServices.Get<ILogger<VideoPreviewHost>>().LogError(ex, "The video preview couldn't be created");
                Error = "The preview couldn't be started.";
            }
        }
        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_surface is not null && control.Handle == _surface.Handle)
        {
            _surface.Dispose();
            _surface = null;
            return;
        }
        base.DestroyNativeControlCore(control);
    }
}
