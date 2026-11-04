using System.Diagnostics;

namespace ValorantMatchTimer.Core;

public static class ShutdownExecutor
{
    public static void ShutdownNow()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/s /t 0",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
}
