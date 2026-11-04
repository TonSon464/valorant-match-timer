using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValorantMatchTimer.Core;

public sealed class AppSettings
{
    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; }

    [JsonPropertyName("shutdown_after_match")]
    public bool ShutdownAfterMatch { get; set; } = true;

    [JsonPropertyName("warning_minutes")]
    public int WarningMinutes { get; set; } = 2;

    [JsonPropertyName("selected_mode")]
    public string SelectedMode { get; set; } = "competitive";

    [JsonPropertyName("custom_duration")]
    public string CustomDuration { get; set; } = "35m";

    [JsonPropertyName("start_with_windows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("cutoff_iso")]
    public string? CutoffIso { get; set; }

    [JsonPropertyName("geometry")]
    public string? Geometry { get; set; }

    [JsonPropertyName("log_path")]
    public string? LogPath { get; set; }

    [JsonPropertyName("show_overlay")]
    public bool ShowOverlay { get; set; } = true;

    [JsonPropertyName("defer_shutdown_in_match")]
    public bool DeferShutdownInMatch { get; set; } = true;

    [JsonPropertyName("overlay_pos")]
    public double[]? OverlayPos { get; set; }

    [JsonPropertyName("overlay_auto_game")]
    public bool OverlayAutoGame { get; set; } = true;

    [JsonPropertyName("overlay_opacity")]
    public double OverlayOpacity { get; set; } = 0.92;

    [JsonPropertyName("overlay_scale")]
    public double OverlayScale { get; set; } = 1.0;

    [JsonPropertyName("overlay_position")]
    public string OverlayPosition { get; set; } = "top-right";

    [JsonPropertyName("overlay_show_time")]
    public bool OverlayShowTime { get; set; } = true;

    [JsonPropertyName("overlay_show_match")]
    public bool OverlayShowMatch { get; set; } = true;

    [JsonPropertyName("overlay_show_end")]
    public bool OverlayShowEnd { get; set; } = true;

    [JsonPropertyName("overlay_show_clock")]
    public bool OverlayShowClock { get; set; } = true;

    [JsonPropertyName("overlay_accent")]
    public string OverlayAccent { get; set; } = "#FF4655";

    [JsonPropertyName("overlay_click_through")]
    public bool OverlayClickThrough { get; set; } = true;

    [JsonIgnore]
    public string EffectiveLogPath =>
        string.IsNullOrWhiteSpace(LogPath) ? AppPaths.DefaultLogPath : LogPath!;

    public void Clamp()
    {
        WarningMinutes = Math.Clamp(WarningMinutes, 1, 15);
        OverlayOpacity = Math.Clamp(OverlayOpacity, 0.2, 1.0);
        OverlayScale = Math.Clamp(OverlayScale, 0.5, 2.0);
        if (string.IsNullOrWhiteSpace(SelectedMode))
        {
            SelectedMode = "competitive";
        }
        if (string.IsNullOrWhiteSpace(CustomDuration))
        {
            CustomDuration = "35m";
        }
        if (string.IsNullOrWhiteSpace(OverlayPosition))
        {
            OverlayPosition = "top-right";
        }
        if (string.IsNullOrWhiteSpace(OverlayAccent))
        {
            OverlayAccent = "#FF4655";
        }
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch
        {
            settings = new AppSettings();
        }

        settings.Clamp();
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            settings.Clamp();
            Directory.CreateDirectory(AppPaths.AppDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch
        {
            // Settings are best-effort; never crash the timer over them.
        }
    }
}
