using System.Runtime.Versioning;
using Luma.Core.Logic;
using Luma.Core.Models;
using Luma.Infrastructure.Platform.Windows.Native;

namespace Luma.Infrastructure.Platform.Windows.Video;

/// <summary>Where the wallpaper windows go: their parent and the sibling they are placed directly below.</summary>
internal sealed record DesktopTarget(nint Parent, nint InsertAfter, string Layout);

/// <summary>What the shell's window tree looks like right now (only the handles that matter).</summary>
internal readonly record struct DesktopSnapshot(nint Progman, nint DefViewInProgman, nint WorkerWInProgman, nint LegacyWorkerW);

/// <summary>
/// Finds the place behind the desktop icons. Explorer draws the wallpaper in a "WorkerW" window that only exists after
/// Progman receives the undocumented-but-stable message 0x052C (used by every live-wallpaper app since Windows 8):
/// <list type="bullet">
/// <item>Windows 11 24H2 and newer: the WorkerW is a <i>child of Progman</i>, placed below SHELLDLL_DefView (the icons).
/// Luma's windows become children of Progman, inserted directly below SHELLDLL_DefView, i.e. above the static wallpaper.</item>
/// <item>Windows 10 and Windows 11 before 24H2: the WorkerW is a top-level window right behind the window that hosts
/// SHELLDLL_DefView. Luma's windows become children of that WorkerW.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DesktopShell
{
    private const uint SpawnWorkerW = 0x052C;
    private const uint SMTO_NORMAL = 0x0;

    /// <summary>Pure decision so both layouts are unit tested.</summary>
    internal static DesktopTarget? Decide(DesktopSnapshot s)
    {
        if (s.Progman == 0) return null;
        if (s.DefViewInProgman != 0)
        {
            // Modern layout (or the WorkerW wasn't created): live directly below the icons inside Progman.
            return new DesktopTarget(s.Progman, s.DefViewInProgman, s.WorkerWInProgman != 0 ? "progman-24h2" : "progman");
        }
        if (s.LegacyWorkerW != 0) return new DesktopTarget(s.LegacyWorkerW, 0, "workerw-classic");
        return null;
    }

    /// <summary>Locates the desktop, asking Explorer to create its WorkerW first. Returns null while Explorer isn't ready.</summary>
    public static DesktopTarget? Locate()
    {
        var progman = User32.FindWindow("Progman", null);
        if (progman == 0) return null;
        User32.SendMessageTimeout(progman, SpawnWorkerW, 0xD, 0x1, SMTO_NORMAL, 1000, out _);
        User32.SendMessageTimeout(progman, SpawnWorkerW, 0, 0, SMTO_NORMAL, 1000, out _);

        var defView = User32.FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        var childWorker = User32.FindWindowEx(progman, 0, "WorkerW", null);
        nint legacy = 0;
        if (defView == 0)
        {
            // Classic layout: the icons live in a top-level WorkerW; the wallpaper WorkerW is the next one in Z order.
            nint candidate = 0, host = 0;
            while ((candidate = User32.FindWindowEx(0, candidate, "WorkerW", null)) != 0)
            {
                if (User32.FindWindowEx(candidate, 0, "SHELLDLL_DefView", null) != 0)
                {
                    host = candidate;
                    break;
                }
            }
            if (host != 0) legacy = User32.FindWindowEx(0, host, "WorkerW", null);
        }
        return Decide(new DesktopSnapshot(progman, defView, childWorker, legacy));
    }

    /// <summary>Connected displays in physical pixels, main display first.</summary>
    public static unsafe IReadOnlyList<DisplayInfo> GetDisplays() => User32.WithPhysicalPixels(() =>
    {
        var list = new List<DisplayInfo>();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(list);
        try
        {
            User32.EnumDisplayMonitors(0, 0, &OnMonitor, System.Runtime.InteropServices.GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return (IReadOnlyList<DisplayInfo>)list.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Bounds.X).ThenBy(d => d.Bounds.Y).ToList();
    });

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe int OnMonitor(nint monitor, nint hdc, RECT* rect, nint param)
    {
        var list = (List<DisplayInfo>)System.Runtime.InteropServices.GCHandle.FromIntPtr(param).Target!;
        var info = new User32.MONITORINFOEXW { cbSize = (uint)sizeof(User32.MONITORINFOEXW) };
        if (User32.GetMonitorInfo(monitor, &info))
        {
            var name = new string(info.szDevice);
            list.Add(new DisplayInfo(name.TrimEnd('\0'), ToPixelRect(info.rcMonitor), ToPixelRect(info.rcWork), (info.dwFlags & 1) != 0));
        }
        return 1;
    }

    /// <summary>
    /// Visible top-level windows that could hide the desktop. Cloaked windows (suspended Store apps, other virtual
    /// desktops), minimised, tool, click-through and no-activate windows (overlays, screen recorders) are ignored.
    /// </summary>
    public static unsafe IReadOnlyList<WindowSnapshot> GetCoveringWindowCandidates() => User32.WithPhysicalPixels(() =>
    {
        var list = new List<WindowSnapshot>();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(list);
        try
        {
            User32.EnumWindows(&OnWindow, System.Runtime.InteropServices.GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return (IReadOnlyList<WindowSnapshot>)list;
    });

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", DesktopWindowThread.OutputClassName, DesktopWindowThread.MonitorClassName
    };

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe int OnWindow(nint hwnd, nint param)
    {
        if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return 1;
        var ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
        if ((ex & (User32.WS_EX_TOOLWINDOW | User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE)) != 0) return 1;
        int cloaked = 0;
        if (User32.DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, &cloaked, sizeof(int)) >= 0 && cloaked != 0) return 1;
        var className = User32.ClassName(hwnd);
        if (IgnoredClasses.Contains(className)) return 1;
        if (!User32.GetWindowRect(hwnd, out var rect)) return 1;

        var list = (List<WindowSnapshot>)System.Runtime.InteropServices.GCHandle.FromIntPtr(param).Target!;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        list.Add(new WindowSnapshot(ToPixelRect(rect), User32.IsZoomed(hwnd), $"{className} (pid {pid}, exstyle 0x{ex:X8})"));
        return 1;
    }

    /// <summary>True when Windows itself reports an exclusive-fullscreen game or presentation mode.</summary>
    public static bool SystemReportsFullscreen()
    {
        if (User32.SHQueryUserNotificationState(out var state) < 0) return false;
        var foreground = User32.GetForegroundWindow();
        return IsFullscreenSignal(state, foreground == 0 ? null : User32.ClassName(foreground));
    }

    /// <summary>
    /// Interprets SHQueryUserNotificationState. QUNS_BUSY (2) is ignored: Windows also reports it whenever the desktop
    /// itself is in the foreground (Explorer's desktop is a window covering the screen), which made the video pause
    /// exactly when the desktop was visible. Borderless fullscreen apps are caught by the window check instead.
    /// Nothing counts while the desktop or taskbar has focus.
    /// </summary>
    internal static bool IsFullscreenSignal(int state, string? foregroundClass)
    {
        if (foregroundClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        return state is 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */ or 4 /* QUNS_PRESENTATION_MODE */;
    }

    public static bool IsOnBattery() => User32.GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    public static PixelRect ToPixelRect(RECT r) => PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
}
