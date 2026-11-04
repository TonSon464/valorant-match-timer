using System.Reflection;
using Microsoft.Win32;

namespace ValorantMatchTimer.Core;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ValorantMatchTimer";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Message) SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return (false, "Could not open the Windows startup registry key.");
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                RemoveLegacyShortcut();
                return (true, "Removed from Windows startup.");
            }

            var command = BuildStartupCommand();
            if (command is null)
            {
                return (false, "Could not determine the app path for startup.");
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            RemoveLegacyShortcut();
            return (true, "Added to Windows startup.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void RemoveLegacyShortcut()
    {
        try
        {
            if (File.Exists(AppPaths.LegacyStartupShortcut))
            {
                File.Delete(AppPaths.LegacyStartupShortcut);
            }
        }
        catch
        {
            // Ignore migration failures.
        }
    }

    private static string? BuildStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(processPath);
        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return null;
            }

            var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (!File.Exists(assemblyPath))
            {
                return null;
            }
            return $"\"{processPath}\" \"{assemblyPath}\"";
        }

        return $"\"{processPath}\"";
    }
}
