using System.Diagnostics;
using System.Text;
using DesktopBuddy.Services;

namespace DesktopBuddy.Services;

public sealed record ForegroundSnapshot(nint Hwnd, uint ProcessId, string ExecutableName, bool IsFullscreen);

public static class ForegroundMonitor
{
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "NotifyIconOverflowWindow"
    };

    public static ForegroundSnapshot? GetCurrent(bool readExecutableName = true)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0) return null;
        return new ForegroundSnapshot(hwnd, processId, readExecutableName ? GetExecutableName(processId) : "", IsFullscreen(hwnd));
    }

    private static string GetExecutableName(uint processId)
    {
        var processHandle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0) return "";
        try
        {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageNameW(processHandle, 0, buffer, ref length)) return "";
            return Path.GetFileName(buffer.ToString());
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }

    private static bool IsFullscreen(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        if (ShellWindowClasses.Contains(className.ToString())) return false;

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect)) return false;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0) return false;
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo)) return false;

        var monitorRect = monitorInfo.rcMonitor;
        var width = monitorRect.Right - monitorRect.Left;
        var height = monitorRect.Bottom - monitorRect.Top;
        if (width <= 0 || height <= 0) return false;

        // A small tolerance handles borderless fullscreen and invisible resize borders.
        var toleranceX = Math.Max(4, (int)(width * 0.01));
        var toleranceY = Math.Max(4, (int)(height * 0.01));
        return windowRect.Left <= monitorRect.Left + toleranceX
            && windowRect.Top <= monitorRect.Top + toleranceY
            && windowRect.Right >= monitorRect.Right - toleranceX
            && windowRect.Bottom >= monitorRect.Bottom - toleranceY;
    }
}
