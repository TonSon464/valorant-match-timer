using System.Windows;
using System.Windows.Interop;
using ValorantMatchTimer.Core;

namespace ValorantMatchTimer.UI;

public static class WindowTheme
{
    public static void EnableDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.EnableDarkTitleBar(hwnd);
        };
    }
}
