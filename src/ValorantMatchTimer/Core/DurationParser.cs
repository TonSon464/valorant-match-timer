using System.Globalization;
using System.Text.RegularExpressions;

namespace ValorantMatchTimer.Core;

public static class DurationParser
{
    private static readonly Regex ClockRegex = new(@"^\s*(\d{1,2}):(\d{2})\s*$", RegexOptions.Compiled);
    private static readonly Regex MinutesRegex = new(@"^\d+(?:\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex UnitRegex = new(
        @"(\d+(?:\.\d+)?)\s*(hours|hour|hrs|hr|h|minutes|minute|mins|min|m|seconds|second|secs|sec|s)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, double> UnitSeconds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h"] = 3600,
        ["hr"] = 3600,
        ["hrs"] = 3600,
        ["hour"] = 3600,
        ["hours"] = 3600,
        ["m"] = 60,
        ["min"] = 60,
        ["mins"] = 60,
        ["minute"] = 60,
        ["minutes"] = 60,
        ["s"] = 1,
        ["sec"] = 1,
        ["secs"] = 1,
        ["second"] = 1,
        ["seconds"] = 1
    };

    public static string FormatHms(double seconds)
    {
        var s = (int)Math.Max(0, seconds);
        var h = s / 3600;
        var m = (s % 3600) / 60;
        var sec = s % 60;
        return $"{h}:{m:00}:{sec:00}";
    }

    public static string FormatMatch(double seconds)
    {
        var s = (int)Math.Max(0, seconds);
        var h = s / 3600;
        var m = (s % 3600) / 60;
        var sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
    }

    public static string FormatDuration(double seconds)
    {
        var s = (int)Math.Max(0, seconds);
        var h = s / 3600;
        var m = (s % 3600) / 60;
        var sec = s % 60;
        if (h > 0) return $"{h}h {m:00}m";
        if (m > 0) return $"{m}m {sec:00}s";
        return $"{sec}s";
    }

    public static int? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().ToLowerInvariant();
        var clock = ClockRegex.Match(t);
        if (clock.Success)
        {
            return int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture) * 60;
        }

        if (MinutesRegex.IsMatch(t))
        {
            return (int)(double.Parse(t, CultureInfo.InvariantCulture) * 60);
        }

        var total = 0.0;
        foreach (Match match in UnitRegex.Matches(t))
        {
            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups[2].Value;
            if (UnitSeconds.TryGetValue(unit, out var multiplier))
            {
                total += value * multiplier;
            }
        }

        return total > 0 ? (int)total : null;
    }

    public static DateTimeOffset? ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ClockRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var target = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        if (target <= now)
        {
            target = target.AddDays(1);
        }
        return target;
    }
}
