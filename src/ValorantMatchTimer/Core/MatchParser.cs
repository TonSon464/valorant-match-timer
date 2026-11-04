using System.Globalization;
using System.Text.RegularExpressions;

namespace ValorantMatchTimer.Core;

public sealed class MatchParser
{
    private static readonly Regex TimestampRegex = new(
        @"^\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d{3})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FlowTransitionRegex = new(
        @"Transitioning from (?<from>\w+) to (?<to>\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FlowBroadcastRegex = new(
        @"Broadcasting state changed to (?<to>\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MapRegex = new(
        @"\[Map Name:\s*([^|\]]+?)\s*\|.*?\[Match Setup:\s*(TRUE|FALSE).*?\[Map Ready:\s*(TRUE|FALSE).*?\[Map Complete:\s*(TRUE|FALSE).*?\[URL:\s*([^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GameRegex = new(
        @"game=([^?\s#]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> MenuMaps = new(StringComparer.Ordinal)
    {
        "MainMenuV2", "Login", "Init", "Entry", "CharacterSelectPersistentLevel",
        "PregameV2", "MainMenu", ""
    };

    public string FlowState { get; private set; } = "";
    public bool InMatch { get; private set; }
    public DateTimeOffset? Start { get; private set; }
    public DateTimeOffset? Last { get; private set; }
    public string MapName { get; private set; } = "";
    public string Broad { get; private set; } = "";
    public string MatchMap { get; private set; } = "";
    public string MatchBroad { get; private set; } = "";
    public bool Setup { get; private set; }
    public bool Ready { get; private set; }
    public bool Complete { get; private set; }

    private bool IsIgnoredMap()
    {
        if (MenuMaps.Contains(MapName))
        {
            return true;
        }
        if (Broad.Contains("ShootingRange", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return MapName.StartsWith("Range", StringComparison.OrdinalIgnoreCase);
    }

    private List<MatchEvent> End(DateTimeOffset? timestamp)
    {
        if (!InMatch)
        {
            return new List<MatchEvent>();
        }

        var end = timestamp ?? DateTimeOffset.Now;
        var start = Start ?? end;
        var duration = Math.Max(0, (end - start).TotalSeconds);
        var evt = new MatchEndEvent(
            start,
            end,
            duration,
            string.IsNullOrWhiteSpace(MatchMap) ? MapName : MatchMap,
            string.IsNullOrWhiteSpace(MatchBroad) ? Broad : MatchBroad);

        InMatch = false;
        Start = null;
        MapName = "";
        Broad = "";
        MatchMap = "";
        MatchBroad = "";
        Setup = false;
        Ready = false;
        Complete = false;

        return new List<MatchEvent> { evt };
    }

    public List<MatchEvent> ForceEnd(DateTimeOffset? timestamp = null) =>
        End(timestamp ?? Last ?? DateTimeOffset.Now);

    public List<MatchEvent> Feed(string line, bool emit = true)
    {
        var events = new List<MatchEvent>();
        var timestamp = ParseLogTimestamp(line);
        if (timestamp.HasValue)
        {
            Last = timestamp;
        }

        var flow = FlowTransitionRegex.Match(line);
        if (!flow.Success)
        {
            flow = FlowBroadcastRegex.Match(line);
        }

        if (flow.Success)
        {
            var newState = flow.Groups["to"].Value;
            FlowState = newState;
            if (emit)
            {
                events.Add(new PhaseEvent(newState));
            }
            if ((newState == "TransitionToMainMenu" || newState == "MainMenu") && InMatch)
            {
                events.AddRange(End(timestamp));
            }
        }

        var map = MapRegex.Match(line);
        if (map.Success)
        {
            MapName = map.Groups[1].Value.Trim();
            Setup = map.Groups[2].Value == "TRUE";
            Ready = map.Groups[3].Value == "TRUE";
            Complete = map.Groups[4].Value == "TRUE";

            var url = map.Groups[5].Value ?? "";
            var game = GameRegex.Match(url);
            Broad = game.Success ? game.Groups[1].Value : "";

            if (MenuMaps.Contains(MapName))
            {
                if (InMatch)
                {
                    events.AddRange(End(timestamp));
                }
            }
            else if (IsIgnoredMap())
            {
                // Range and other non-match maps do not start a timer.
            }
            else if (Setup && Ready && !Complete && !InMatch)
            {
                InMatch = true;
                Start = timestamp;
                MatchMap = MapName;
                MatchBroad = Broad;
                if (emit)
                {
                    events.Add(new MatchStartEvent(
                        timestamp ?? DateTimeOffset.Now,
                        MapName,
                        Broad));
                }
            }
            else if (Complete && InMatch)
            {
                events.AddRange(End(timestamp));
            }
        }

        return events;
    }

    private static DateTimeOffset? ParseLogTimestamp(string line)
    {
        var match = TimestampRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                match.Groups[1].Value,
                "yyyy.MM.dd-HH.mm.ss:fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utc))
        {
            return null;
        }

        return new DateTimeOffset(utc).ToLocalTime();
    }
}
