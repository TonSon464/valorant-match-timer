namespace ValorantMatchTimer.Core;

public static class AppPaths
{
    public static string AppDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ValorantMatchTimer");

    public static string SettingsFile => Path.Combine(AppDir, "settings.json");

    public static string HistoryFile => Path.Combine(AppDir, "history.json");

    public static string DefaultLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VALORANT",
        "Saved",
        "Logs",
        "ShooterGame.log");

    public static string LegacyStartupShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Start Menu",
        "Programs",
        "Startup",
        "Valorant Match Timer.lnk");
}
