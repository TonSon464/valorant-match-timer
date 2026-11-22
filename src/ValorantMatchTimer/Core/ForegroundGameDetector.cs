using System.Diagnostics;

namespace ValorantMatchTimer.Core;

public static class ForegroundGameDetector
{
    private static readonly HashSet<string> NonGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ShellExperienceHost",
        "SearchHost",
        "StartMenuExperienceHost",
        "LockApp",
        "ApplicationFrameHost",
        "SystemSettings",
        "TextInputHost",
        "backgroundTaskHost",
        "RuntimeBroker",
        "dwm",
        "Taskmgr",
        "ValorantMatchTimer",
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera",
        "vivaldi",
        "vlc",
        "wmplayer",
        "mpc-hc64",
        "mpc-be64",
        "potplayermini64",
        "Spotify",
        "Discord",
        "Telegram",
        "Code",
        "devenv"
    };

    public static bool IsFullscreenGame(out string processName, bool includeBorderless = false)
    {
        processName = "";
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch
        {
            return false;
        }

        if (NonGameProcesses.Contains(processName))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 320 || height < 240)
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        const int tolerance = 2;
        var screen = info.rcMonitor;
        var coversMonitor = rect.Left <= screen.Left + tolerance
            && rect.Top <= screen.Top + tolerance
            && rect.Right >= screen.Right - tolerance
            && rect.Bottom >= screen.Bottom - tolerance;
        if (!coversMonitor)
        {
            return false;
        }

        if (includeBorderless)
        {
            return true;
        }

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var hasChrome = (style & (NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME)) != 0;
        var isTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        return !hasChrome && isTopmost;
    }
}
