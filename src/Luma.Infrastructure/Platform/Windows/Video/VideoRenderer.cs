using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Luma.Core.Enums;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Infrastructure.Platform.Windows.Native;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>A window the renderer draws into, sized in physical pixels.</summary>
internal sealed record RenderTarget(nint Window, int Width, int Height, WallpaperFitMode Fit);

/// <summary>
/// Owns one <see cref="MediaEngineSession"/> and a render thread. While playing, the thread wakes once per display refresh
/// (DwmFlush), asks the engine whether a new frame is ready and, only then, copies it into each output's swap chain.
/// While paused, or with no outputs, the thread sleeps on an event and costs nothing. All engine and Direct3D calls
/// happen on this one thread; other threads talk to it through a command queue.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class VideoRenderer : IDisposable
{
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<SwapChainOutput> _outputs = [];
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _framePresented = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MediaEngineSession? _session;
    private (int Width, int Height) _videoSize;
    private bool _playing;
    private bool _redraw;
    private bool _broken;
    private volatile bool _stopping;
    private volatile string _filePath = string.Empty;

    public VideoRenderer(ILogger logger, string name)
    {
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Raised on the render or a Media Foundation thread once; the renderer stops drawing afterwards.</summary>
    public event Action<VideoHostFailure>? Failed;

    /// <summary>Number of frames presented (diagnostics and tests).</summary>
    public long FramesPresented { get; private set; }

    /// <summary>Opens the file. Completes when the first frame can be drawn; faults with a user-facing message.</summary>
    public Task LoadAsync(string filePath)
    {
        _filePath = filePath;
        Post(() =>
        {
            try
            {
                _session = MediaEngineSession.Create();
                _session.EventReceived += OnEngineEvent;
                _session.Load(filePath);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _loaded.TrySetException(new Core.Exceptions.VideoHostException(MediaErrors.Describe((4, ex.HResult), filePath), recoverable: false, ex));
            }
        });
        return _loaded.Task;
    }


    /// <summary>Replaces the outputs. The current frame is drawn into the new ones straight away, even while paused.</summary>
    public Task SetTargetsAsync(IReadOnlyList<RenderTarget> targets) => Invoke(() =>
    {
        DisposeOutputs();
        if (_session is null) return;
        foreach (var target in targets)
        {
            try
            {
                _outputs.Add(SwapChainOutput.Create(_session, target));
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "A swap chain couldn't be created for window {Window:X}", target.Window);
                throw new Core.Exceptions.VideoHostException("The graphics card couldn't create a video surface.", recoverable: true, ex);
            }
        }
        _framePresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _redraw = true;
    });

    /// <summary>Resizes the output drawing into <paramref name="window"/> (preview windows resize with Luma's layout).</summary>
    public void Resize(nint window, int width, int height) => Post(() =>
    {
        foreach (var output in _outputs.Where(o => o.Window == window))
        {
            if (output.Resize(width, height) < 0) Fail("The video surface couldn't be resized.", recoverable: true);
        }
        _redraw = true;
    });

    /// <summary>Completes once a frame has been presented on every current output.</summary>
    public Task WaitForFrameAsync() => Invoke(() => _framePresented.Task).Unwrap();

    public void SetPlaying(bool playing) => Post(() =>
    {
        _playing = playing;
        if (_session is null || _broken) return;
        try
        {
            if (_loaded.Task.IsCompletedSuccessfully) _session.SetPlaying(playing);
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Changing the playback state failed");
            Fail("The video player stopped responding.", recoverable: true);
        }
    });

    private void OnEngineEvent(MediaEngineEvent e, nint param1, uint param2)
    {
        switch (e)
        {
            case MediaEngineEvent.LoadedData:
                Post(() =>
                {
                    if (_session is null) return;
                    _session.DisableAudioStreams();
                    _videoSize = _session.NativeSize;
                    if (_playing) _session.SetPlaying(true);
                    _redraw = true;
                    _loaded.TrySetResult();
                });
                break;
            case MediaEngineEvent.FirstFrameReady:
            case MediaEngineEvent.Seeked:
                Post(() => _redraw = true);
                break;
            case MediaEngineEvent.Error:
                Post(() =>
                {
                    var error = _session?.ReadError() ?? (0, 0);
                    var message = MediaErrors.Describe(error, _filePath);
                    // A file that never opened is not worth retrying; a decode error mid-playback might be transient.
                    if (_loaded.TrySetException(new Core.Exceptions.VideoHostException(message, recoverable: false))) return;
                    _logger.LogWarning("Playback error {Code} (0x{Extended:X8})", error.Code, error.Extended);
                    Fail(message, recoverable: true);
                });
                break;
        }
    }

    private void Run()
    {
        var pacing = Stopwatch.StartNew();
        var fastFlushes = 0;
        while (!_stopping)
        {
            DrainCommands();
            if (_stopping) break;

            var canDraw = _session is not null && !_broken && _outputs.Count > 0 && _loaded.Task.IsCompletedSuccessfully;
            if (canDraw && _redraw)
            {
                _redraw = false;
                Present();
                continue;
            }
            if (!canDraw || !_playing)
            {
                _wake.WaitOne();
                continue;
            }

            // Pace to the compositor. DwmFlush returns at once when composition is off (screen off, remote edge
            // cases); never let that turn into a busy loop.
            pacing.Restart();
            NativeMethods.DwmFlush();
            if (pacing.ElapsedMilliseconds < 2)
            {
                if (++fastFlushes > 3) _wake.WaitOne(16);
            }
            else
            {
                fastFlushes = 0;
            }

            if (_session!.TryGetNewFrame()) Present();
        }

        DisposeOutputs();
        _session?.Dispose();
        _session = null;
    }

    private void Present()
    {
        var presented = 0;
        foreach (var output in _outputs.ToList())
        {
            var layout = VideoLayout.Compute(_videoSize.Width, _videoSize.Height, output.Width, output.Height, output.Fit);
            var hr = output.Present(_session!, layout);
            if (hr >= 0)
            {
                presented++;
                continue;
            }

            if (!IsWindow(output.Window))
            {
                // The desktop host destroyed this window (Explorer restarted); the host rebuilds the outputs.
                output.Dispose();
                _outputs.Remove(output);
                continue;
            }
            if (hr is NativeMethods.DXGI_ERROR_DEVICE_REMOVED or NativeMethods.DXGI_ERROR_DEVICE_RESET)
            {
                Fail("The graphics driver was reset.", recoverable: true);
                return;
            }
            _logger.LogWarning("Presenting a video frame failed (0x{Hr:X8})", hr);
            Fail("A video frame couldn't be drawn.", recoverable: true);
            return;
        }
        if (presented > 0)
        {
            FramesPresented++;
            if (presented == _outputs.Count) _framePresented.TrySetResult();
        }
    }

    private void Fail(string message, bool recoverable)
    {
        if (_broken) return;
        _broken = true;
        _framePresented.TrySetException(new Core.Exceptions.VideoHostException(message, recoverable));
        Failed?.Invoke(new VideoHostFailure(message, recoverable));
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A video renderer command failed");
            }
        }
    }

    private void Post(Action action)
    {
        _commands.Enqueue(action);
        _wake.Set();
    }

    private Task Invoke(Action action) => Invoke(() =>
    {
        action();
        return true;
    });

    private Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stopping)
        {
            tcs.SetException(new ObjectDisposedException(nameof(VideoRenderer)));
            return tcs.Task;
        }
        Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private void DisposeOutputs()
    {
        foreach (var output in _outputs) output.Dispose();
        _outputs.Clear();
    }

    /// <summary>Stops the render thread and releases the decoder, swap chains and device. Blocks until done (bounded).</summary>
    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _loaded.TrySetCanceled();
        _wake.Set();
        _framePresented.TrySetCanceled();
        if (Thread.CurrentThread == _thread) return; // the loop exits by itself and releases everything
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("The video render thread didn't stop within 5 seconds");
            return; // the thread may still touch the event; its finalizer closes it
        }
        _wake.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
}

/// <summary>A flip-model swap chain on one window. The back buffer is cached; D3D11 tracks the flip internally.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class SwapChainOutput : IDisposable
{
    private nint _swapChain;
    private nint _backBuffer;

    private SwapChainOutput(nint window, WallpaperFitMode fit)
    {
        Window = window;
        Fit = fit;
    }

    public nint Window { get; }
    public WallpaperFitMode Fit { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public static SwapChainOutput Create(MediaEngineSession session, RenderTarget target)
    {
        var output = new SwapChainOutput(target.Window, target.Fit) { Width = Math.Max(1, target.Width), Height = Math.Max(1, target.Height) };
        nint dxgiDevice = 0, adapter = 0, factory = 0;
        try
        {
            Com.Check(Com.QueryInterface(session.Device, Iid.DXGIDevice, out dxgiDevice), "Querying IDXGIDevice");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(dxgiDevice, 7))(dxgiDevice, &adapter), "GetAdapter");
            var factoryIid = Iid.DXGIFactory2;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Slot(adapter, 6))(adapter, &factoryIid, &factory), "GetParent(IDXGIFactory2)");

            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)output.Width, Height = (uint)output.Height, Format = NativeMethods.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, BufferUsage = 0x20 /* RENDER_TARGET_OUTPUT */, BufferCount = 2,
                Scaling = 0 /* STRETCH */, SwapEffect = 4 /* FLIP_DISCARD */, AlphaMode = 0
            };
            nint swapChain;
            // IDXGIFactory2::CreateSwapChainForHwnd (slot 15)
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, DXGI_SWAP_CHAIN_DESC1*, nint, nint, nint*, int>)Com.Slot(factory, 15))(
                factory, session.Device, target.Window, &desc, 0, 0, &swapChain);
            if (hr < 0)
            {
                // Older drivers or unusual windows: fall back to the blit model.
                desc.SwapEffect = 0; // DISCARD
                desc.BufferCount = 1;
                hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, DXGI_SWAP_CHAIN_DESC1*, nint, nint, nint*, int>)Com.Slot(factory, 15))(
                    factory, session.Device, target.Window, &desc, 0, 0, &swapChain);
            }
            Com.Check(hr, "CreateSwapChainForHwnd");
            output._swapChain = swapChain;
            // IDXGIFactory::MakeWindowAssociation (slot 8): never let DXGI react to Alt+Enter on Luma's windows.
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Com.Slot(factory, 8))(factory, target.Window, 0x1 | 0x2);
            Com.Check(output.GetBackBuffer(), "GetBuffer");
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            Com.Release(factory);
            Com.Release(adapter);
            Com.Release(dxgiDevice);
        }
    }

    private int GetBackBuffer()
    {
        var iid = Iid.D3D11Texture2D;
        nint buffer;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Com.Slot(_swapChain, 9))(_swapChain, 0, &iid, &buffer);
        if (hr >= 0) _backBuffer = buffer;
        return hr;
    }

    public int Present(MediaEngineSession session, VideoFrameLayout layout)
    {
        var hr = session.TransferFrame(_backBuffer, layout);
        if (hr < 0) return hr;
        return ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Com.Slot(_swapChain, 8))(_swapChain, 0, 0); // Present
    }

    public int Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return 0;
        Com.ReleaseAndClear(ref _backBuffer);
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, int>)Com.Slot(_swapChain, 13))(_swapChain, 0, (uint)width, (uint)height, 0, 0);
        if (hr < 0) return hr;
        Width = width;
        Height = height;
        return GetBackBuffer();
    }

    public void Dispose()
    {
        Com.ReleaseAndClear(ref _backBuffer);
        Com.ReleaseAndClear(ref _swapChain);
    }
}
