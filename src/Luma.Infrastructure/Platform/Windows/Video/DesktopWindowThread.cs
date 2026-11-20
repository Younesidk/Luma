using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Luma.Core.Exceptions;
using Luma.Core.Models;
using Luma.Infrastructure.Platform.Windows.Native;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>A wallpaper window placed on the desktop for one display.</summary>
internal sealed record DesktopOutput(nint Window, DisplayInfo Display, int Width, int Height);

/// <summary>
/// A dedicated thread with its own message loop that owns every window Luma puts on the desktop. Keeping these windows
/// off Avalonia's UI thread matters: a child window of Explorer's desktop shares input processing with Explorer, so a
/// busy UI thread must never be able to stall the desktop (and vice versa).
/// A hidden top-level window receives the system broadcasts this feature reacts to: Explorer restarts (TaskbarCreated),
/// display changes, power source and display power changes, sleep/resume and session lock.
/// Events are raised on this thread; handlers must return quickly.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class DesktopWindowThread : IDisposable
{
    public const string OutputClassName = "LumaVideoWallpaperOutput";
    public const string MonitorClassName = "LumaVideoWallpaperMonitor";

    private const uint WM_INVOKE = User32.WM_APP + 1;
    private const nuint OcclusionTimer = 1;
    private const nuint DisplayDebounceTimer = 2;
    private const nuint ZOrderQuickTimer = 3;
    private const nuint ZOrderSettledTimer = 4;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;

    private static readonly object ClassLock = new();
    private static bool _classesRegistered;
    private static DesktopWindowThread? _current; // window procedures are static; there is one window thread per process

    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DesktopOutput> _outputs = [];
    private readonly Dictionary<nint, VideoStill> _stills = [];
    private DesktopTarget? _target;
    private nint _monitor;
    private uint _taskbarCreated;
    private nint _powerSourceNotification;
    private nint _displayStateNotification;
    private bool _destroyingOutputs;

    public DesktopWindowThread(ILogger logger)
    {
        if (Interlocked.CompareExchange(ref _current, this, null) is not null)
            throw new InvalidOperationException("Only one desktop window thread may exist.");
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Luma desktop windows" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.Wait(TimeSpan.FromSeconds(10));
        if (!_ready.Task.IsCompletedSuccessfully) throw new VideoHostException("Luma couldn't start its desktop window thread.", recoverable: false);
    }

    public event Action? ShellRestarted;
    public event Action? DisplaysChanged;
    public event Action? OcclusionTick;
    public event Action? OutputLost;
    public event Action<bool>? PowerSourceChanged;   // true = on battery
    public event Action<bool>? DisplayPowerChanged;  // true = displays off
    public event Action<bool>? SessionLockChanged;   // true = locked
    public event Action<bool>? SuspendChanged;       // true = going to sleep

    public IReadOnlyList<DesktopOutput> Outputs => _outputs;

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
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
        if (!User32.PostMessage(_monitor, WM_INVOKE, 0, 0)) tcs.TrySetException(new VideoHostException("The desktop window thread isn't running."));
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>Creates hidden wallpaper windows behind the icons, one per display. Must run on this thread.</summary>
    public IReadOnlyList<DesktopOutput> CreateOutputs(IReadOnlyList<DisplayInfo> displays)
    {
        DestroyOutputs();
        var target = DesktopShell.Locate() ?? throw new VideoHostException("Windows' desktop isn't available right now (Explorer may be restarting).");
        if (!User32.GetWindowRect(target.Parent, out var parentRect)) throw new VideoHostException("Windows' desktop window couldn't be measured.");
        _target = target;

        foreach (var display in displays)
        {
            var bounds = Core.Logic.VideoLayout.ToParentCoordinates(display.Bounds, parentRect.Left, parentRect.Top);
            var hwnd = User32.CreateWindowEx(User32.WS_EX_NOACTIVATE, OutputClassName, "Luma live wallpaper",
                User32.WS_CHILD | User32.WS_CLIPSIBLINGS, bounds.X, bounds.Y, bounds.Width, bounds.Height, target.Parent, 0,
                User32.GetModuleHandle(0), 0);
            if (hwnd == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                DestroyOutputs();
                throw new VideoHostException($"Luma couldn't place a window on the desktop (error {error}).");
            }
            PlaceInZOrder(hwnd, target);
            _outputs.Add(new DesktopOutput(hwnd, display, bounds.Width, bounds.Height));
        }
        _logger.LogInformation("Desktop outputs created ({Layout}): {Count} window(s) under {Parent:X}", target.Layout, _outputs.Count, target.Parent);
        return _outputs.ToList();
    }

    /// <summary>Makes the windows visible (called once the first frame is in their swap chains, so nothing flashes).</summary>
    public void ShowOutputs()
    {
        foreach (var output in _outputs)
        {
            User32.ShowWindow(output.Window, User32.SW_SHOWNA);
            if (_target is not null) PlaceInZOrder(output.Window, _target);
        }
    }

    /// <summary>
    /// Windows 11 24H2+: when the Windows wallpaper changes (Luma setting the video's still frame, a slideshow, Settings),
    /// Explorer creates a new wallpaper WorkerW inside Progman and inserts it <i>above</i> Luma's windows, hiding the
    /// video behind a still image while it keeps playing. This puts Luma's windows back directly below the icons.
    /// Cheap (walks Progman's few children); returns true when windows had to be moved.
    /// </summary>
    public bool EnsureAboveWallpaper()
    {
        if (_target is not { InsertAfter: not 0 } target || _outputs.Count == 0 || !User32.IsWindow(target.Parent)) return false;
        var defView = User32.FindWindowEx(target.Parent, 0, "SHELLDLL_DefView", null);
        if (defView == 0) return false;

        if (IsDirectlyBelow(defView, _outputs.Select(o => o.Window).ToHashSet())) return false;

        _target = target with { InsertAfter = defView };
        foreach (var output in _outputs) PlaceInZOrder(output.Window, _target);
        return true;
    }

    /// <summary>True when the windows immediately following <paramref name="anchor"/> are exactly <paramref name="windows"/>.</summary>
    internal static bool IsDirectlyBelow(nint anchor, IReadOnlySet<nint> windows)
    {
        var next = User32.GetWindow(anchor, User32.GW_HWNDNEXT);
        for (var i = 0; i < windows.Count; i++)
        {
            if (next == 0 || !windows.Contains(next)) return false;
            next = User32.GetWindow(next, User32.GW_HWNDNEXT);
        }
        return true;
    }

    /// <summary>True when every output window and its desktop parent still exist.</summary>
    public bool OutputsAreHealthy() =>
        _outputs.Count > 0 && _target is not null && User32.IsWindow(_target.Parent) && _outputs.All(o => User32.IsWindow(o.Window));

    /// <summary>
    /// Makes the output windows paint these pictures themselves (GDI) while no swap chain is attached, so the desktop keeps
    /// showing the paused frame after the decoder was released. Must run on this thread, after the swap chains are gone.
    /// </summary>
    public void SetStills(IReadOnlyList<VideoStill> stills)
    {
        _stills.Clear();
        foreach (var still in stills)
        {
            if (!_outputs.Any(o => o.Window == still.Window)) continue;
            _stills[still.Window] = still;
            User32.InvalidateRect(still.Window, 0, false);
        }
    }

    /// <summary>Drops the pictures (a swap chain draws the windows again). Must run on this thread.</summary>
    public void ClearStills() => _stills.Clear();

    public bool HasStills => _stills.Count > 0;

    private void PaintOutput(nint hwnd)
    {
        User32.PAINTSTRUCT paint;
        var hdc = User32.BeginPaint(hwnd, &paint);
        try
        {
            if (hdc == 0 || !_stills.TryGetValue(hwnd, out var still) || !User32.GetClientRect(hwnd, out var client)) return;
            var header = new User32.BITMAPINFOHEADER
            {
                Size = (uint)sizeof(User32.BITMAPINFOHEADER), Width = still.Width, Height = -still.Height /* top-down */, Planes = 1, BitCount = 32
            };
            User32.SetStretchBltMode(hdc, User32.HALFTONE);
            fixed (byte* bits = still.Pixels)
            {
                User32.StretchDIBits(hdc, 0, 0, client.Width, client.Height, 0, 0, still.Width, still.Height, bits, &header, 0 /* DIB_RGB_COLORS */, User32.SRCCOPY);
            }
        }
        finally
        {
            User32.EndPaint(hwnd, &paint);
        }
    }

    public void DestroyOutputs()
    {
        _stills.Clear();
        _destroyingOutputs = true;
        try
        {
            foreach (var output in _outputs)
            {
                if (User32.IsWindow(output.Window)) User32.DestroyWindow(output.Window);
            }
        }
        finally
        {
            _outputs.Clear();
            _destroyingOutputs = false;
        }
    }

    public void SetOcclusionPolling(bool enabled)
    {
        if (enabled) User32.SetTimer(_monitor, OcclusionTimer, 1000, 0);
        else User32.KillTimer(_monitor, OcclusionTimer);
    }

    private static void PlaceInZOrder(nint hwnd, DesktopTarget target) =>
        User32.SetWindowPos(hwnd, target.InsertAfter, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);

    private void Run()
    {
        try
        {
            User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            RegisterClasses();
            _monitor = User32.CreateWindowEx(User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE, MonitorClassName, "Luma live wallpaper monitor",
                User32.WS_POPUP, 0, 0, 0, 0, 0, 0, User32.GetModuleHandle(0), 0);
            if (_monitor == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");

            _taskbarCreated = User32.RegisterWindowMessage("TaskbarCreated");
            User32.WTSRegisterSessionNotification(_monitor, 0 /* NOTIFY_FOR_THIS_SESSION */);
            _powerSourceNotification = User32.RegisterPowerSettingNotification(_monitor, User32.GUID_ACDC_POWER_SOURCE, 0);
            _displayStateNotification = User32.RegisterPowerSettingNotification(_monitor, User32.GUID_CONSOLE_DISPLAY_STATE, 0);
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The desktop window thread couldn't start");
            _ready.TrySetException(ex);
            return;
        }

        User32.MSG msg;
        while (User32.GetMessage(&msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(&msg);
            User32.DispatchMessage(&msg);
        }

        DestroyOutputs();
        if (_powerSourceNotification != 0) User32.UnregisterPowerSettingNotification(_powerSourceNotification);
        if (_displayStateNotification != 0) User32.UnregisterPowerSettingNotification(_displayStateNotification);
        User32.WTSUnRegisterSessionNotification(_monitor);
        User32.DestroyWindow(_monitor);
    }

    private static void RegisterClasses()
    {
        lock (ClassLock)
        {
            if (_classesRegistered) return;
            Register(OutputClassName, &OutputProc);
            Register(MonitorClassName, &MonitorProc);
            _classesRegistered = true;
        }

        static void Register(string name, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> proc)
        {
            fixed (char* className = name)
            {
                var wc = new User32.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(User32.WNDCLASSEXW), lpfnWndProc = proc, hInstance = User32.GetModuleHandle(0), lpszClassName = className
                };
                if (User32.RegisterClassEx(&wc) == 0 && Marshal.GetLastPInvokeError() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                    throw new InvalidOperationException($"RegisterClassEx({name}) failed ({Marshal.GetLastPInvokeError()})");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OutputProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_NCHITTEST:
                return User32.HTTRANSPARENT; // clicks always reach the icons / desktop
            case User32.WM_MOUSEACTIVATE:
                return User32.MA_NOACTIVATE;
            case User32.WM_ERASEBKGND:
                return 1;
            case User32.WM_PAINT:
                // Normally the swap chain draws the content; while the decoder is released the window paints the paused frame.
                if (_current is { HasStills: true } owner) owner.PaintOutput(hwnd);
                else User32.ValidateRect(hwnd, 0);
                return 0;
            case User32.WM_DESTROY:
                _current?.OnOutputDestroyed(hwnd);
                break;
        }
        return User32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MonitorProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = _current;
        if (self is null) return User32.DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            return self.HandleMonitorMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            self._logger.LogError(ex, "Handling a desktop notification failed");
            return User32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private nint HandleMonitorMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _logger.LogInformation("Explorer restarted (TaskbarCreated)");
            ShellRestarted?.Invoke();
            return 0;
        }

        switch (msg)
        {
            case WM_INVOKE:
                while (_queue.TryDequeue(out var action)) action();
                return 0;
            case User32.WM_DISPLAYCHANGE:
                // Several messages arrive while Windows rearranges displays; act once things settle.
                User32.SetTimer(hwnd, DisplayDebounceTimer, 750, 0);
                return 0;
            case User32.WM_TIMER when wParam == (nint)OcclusionTimer:
                RestoreZOrder("periodic check");
                OcclusionTick?.Invoke();
                return 0;
            case User32.WM_TIMER when wParam == (nint)ZOrderQuickTimer || wParam == (nint)ZOrderSettledTimer:
                User32.KillTimer(hwnd, (nuint)wParam);
                RestoreZOrder("wallpaper changed");
                return 0;
            case User32.WM_SETTINGCHANGE when wParam == (nint)SPI_SETDESKWALLPAPER:
                // Explorer inserts its new wallpaper window within ~100 ms and removes the old one ~1.2 s later.
                User32.SetTimer(hwnd, ZOrderQuickTimer, 150, 0);
                User32.SetTimer(hwnd, ZOrderSettledTimer, 1500, 0);
                break;
            case User32.WM_TIMER when wParam == (nint)DisplayDebounceTimer:
                User32.KillTimer(hwnd, DisplayDebounceTimer);
                DisplaysChanged?.Invoke();
                return 0;
            case User32.WM_WTSSESSION_CHANGE:
                if (wParam == User32.WTS_SESSION_LOCK) SessionLockChanged?.Invoke(true);
                else if (wParam == User32.WTS_SESSION_UNLOCK) SessionLockChanged?.Invoke(false);
                return 0;
            case User32.WM_POWERBROADCAST:
                HandlePower((uint)wParam, lParam);
                return 1;
            // Other WM_SETTINGCHANGE broadcasts are ignored: output windows only depend on display bounds, and work areas
            // are re-read on each occlusion check.
        }
        return User32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void RestoreZOrder(string reason)
    {
        if (EnsureAboveWallpaper())
            _logger.LogInformation("Explorer put its wallpaper window above the video ({Reason}); moved the video back below the icons", reason);
    }

    private void HandlePower(uint kind, nint lParam)
    {
        switch (kind)
        {
            case User32.PBT_APMSUSPEND:
                SuspendChanged?.Invoke(true);
                break;
            case User32.PBT_APMRESUMESUSPEND:
            case User32.PBT_APMRESUMEAUTOMATIC:
                SuspendChanged?.Invoke(false);
                break;
            case User32.PBT_POWERSETTINGCHANGE when lParam != 0:
                var setting = (User32.POWERBROADCAST_SETTING*)lParam;
                if (setting->PowerSetting == User32.GUID_ACDC_POWER_SOURCE) PowerSourceChanged?.Invoke(setting->Data != 0);
                else if (setting->PowerSetting == User32.GUID_CONSOLE_DISPLAY_STATE) DisplayPowerChanged?.Invoke(setting->Data == 0);
                break;
        }
    }

    private void OnOutputDestroyed(nint hwnd)
    {
        if (_destroyingOutputs) return;
        var removed = _outputs.RemoveAll(o => o.Window == hwnd);
        if (removed > 0)
        {
            // Not destroyed by Luma: Explorer's desktop window went away (Explorer crashed or restarted).
            _logger.LogWarning("A wallpaper window was destroyed by the system (desktop recreated)");
            OutputLost?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_monitor != 0) User32.PostMessage(_monitor, WM_INVOKE, 0, 0);
        _queue.Enqueue(() => User32.PostQuitMessage(0));
        if (_monitor != 0) User32.PostMessage(_monitor, WM_INVOKE, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
        Interlocked.CompareExchange(ref _current, null, this);
    }
}
