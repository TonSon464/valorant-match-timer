namespace ValorantMatchTimer.Core;

public sealed record GameMode(string Key, string Label, int Seconds, string[] BroadHints)
{
    public int Minutes => Seconds / 60;
}

public static class GameModes
{
    public static IReadOnlyList<GameMode> All { get; } = new List<GameMode>
    {
        new("competitive", "Competitive", 40 * 60, Array.Empty<string>()),
        new("unrated", "Unrated", 35 * 60, Array.Empty<string>()),
        new("swiftplay", "Swiftplay", 15 * 60, Array.Empty<string>()),
        new("spikerush", "Spike Rush", 10 * 60, Array.Empty<string>()),
        new("deathmatch", "Deathmatch", 10 * 60, new[] { "deathmatch" }),
        new("tdm", "Team Deathmatch", 12 * 60, new[] { "hurm" }),
        new("escalation", "Escalation", 10 * 60, new[] { "ggteam" }),
        new("custom", "Custom", 35 * 60, Array.Empty<string>())
    };

    private static readonly Dictionary<string, GameMode> ByKey =
        All.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

    public static GameMode Get(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key, out var mode))
        {
            return mode;
        }

        return ByKey["custom"];
    }

    public static bool IsKnown(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ByKey.ContainsKey(key);

    public static string KeyForBroad(string? broad, string fallback)
    {
        var b = (broad ?? string.Empty).ToLowerInvariant();
        if (b.Contains("hurm", StringComparison.Ordinal))
        {
            return "tdm";
        }
        if (b.Contains("deathmatch", StringComparison.Ordinal))
        {
            return "deathmatch";
        }
        if (b.Contains("ggteam", StringComparison.Ordinal))
        {
            return "escalation";
        }
        return fallback;
    }

    public static string ShortName(string? broad)
    {
        var b = (broad ?? string.Empty).ToLowerInvariant();
        if (b.Contains("hurm", StringComparison.Ordinal)) return "Team Deathmatch";
        if (b.Contains("deathmatch", StringComparison.Ordinal)) return "Deathmatch";
        if (b.Contains("shootingrange", StringComparison.Ordinal) || b.Contains("range", StringComparison.Ordinal)) return "Range";
        if (b.Contains("bomb", StringComparison.Ordinal)) return "Bomb";

        var last = (broad ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
        var name = last.Replace("GameMode_C", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }
}
