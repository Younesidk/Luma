using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Infrastructure.Platform.Windows.Native;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>Creates preview players that live inside Luma's own window (hosted by the UI as a native child control).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVideoPreviewFactory(ILogger<WindowsVideoPreviewFactory> logger) : IVideoPreviewFactory
{
    private bool? _supported;

    public bool IsSupported => _supported ??= WindowsVideoSupport.Check().IsSupported;

    public IVideoPreviewSurface Create(nint parentWindow, string filePath) => new WindowsVideoPreviewSurface(parentWindow, filePath, logger);
}

/// <summary>
/// A child window of Luma's main window that plays a video muted and looping, letterboxed (Fit) so the whole frame is
/// visible. It must be created and disposed on the UI thread that owns <c>parentWindow</c>; drawing happens on the
/// renderer's own thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsVideoPreviewSurface : IVideoPreviewSurface
{
    private const string ClassName = "LumaVideoPreview";
    private static readonly object ClassLock = new();
    private static bool _registered;
    private static readonly ConcurrentDictionary<nint, WindowsVideoPreviewSurface> Surfaces = new();

    private readonly VideoRenderer _renderer;
    private readonly ILogger _logger;
    private bool _disposed;

    public WindowsVideoPreviewSurface(nint parentWindow, string filePath, ILogger logger)
    {
        _logger = logger;
        RegisterClass();
        User32.GetClientRect(parentWindow, out var parentRect);
        Handle = User32.CreateWindowEx(0, ClassName, "Luma video preview", User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_CLIPSIBLINGS,
            0, 0, Math.Max(1, parentRect.Width), Math.Max(1, parentRect.Height), parentWindow, 0, User32.GetModuleHandle(0), 0);
        if (Handle == 0) throw new InvalidOperationException($"The preview window couldn't be created ({Marshal.GetLastPInvokeError()}).");
        Surfaces[Handle] = this;

        _renderer = new VideoRenderer(logger, "Luma video preview");
        _renderer.Failed += f => Failed?.Invoke(this, f.Message);
        _ = StartAsync(filePath);
    }

    public nint Handle { get; }

    public event EventHandler<string>? Failed;

    private async Task StartAsync(string filePath)
    {
        try
        {
            await _renderer.LoadAsync(filePath);
            User32.GetClientRect(Handle, out var rect);
            await _renderer.SetTargetsAsync([new RenderTarget(Handle, Math.Max(1, rect.Width), Math.Max(1, rect.Height), WallpaperFitMode.Fit)]);
            _renderer.SetPlaying(true);
        }
        catch (Exception ex) when (ex is Core.Exceptions.VideoHostException or ObjectDisposedException or TaskCanceledException)
        {
            if (!_disposed && ex is Core.Exceptions.VideoHostException) Failed?.Invoke(this, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The video preview failed");
            if (!_disposed) Failed?.Invoke(this, "The preview couldn't be played.");
        }
    }

    public void SetPaused(bool paused) => _renderer.SetPlaying(!paused);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.Dispose();       // releases the swap chain before its window goes away
        Surfaces.TryRemove(Handle, out _);
        User32.DestroyWindow(Handle);
    }

    private static unsafe void RegisterClass()
    {
        lock (ClassLock)
        {
            if (_registered) return;
            fixed (char* name = ClassName)
            {
                var wc = new User32.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(User32.WNDCLASSEXW), lpfnWndProc = &WindowProc, hInstance = User32.GetModuleHandle(0), lpszClassName = name
                };
                if (User32.RegisterClassEx(&wc) == 0 && Marshal.GetLastPInvokeError() != 1410)
                    throw new InvalidOperationException("The preview window class couldn't be registered.");
            }
            _registered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_SIZE when Surfaces.TryGetValue(hwnd, out var surface):
                surface._renderer.Resize(hwnd, (int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));
                return 0;
            case User32.WM_ERASEBKGND:
                return 1;
            case User32.WM_PAINT:
                User32.ValidateRect(hwnd, 0);
                return 0;
        }
        return User32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
