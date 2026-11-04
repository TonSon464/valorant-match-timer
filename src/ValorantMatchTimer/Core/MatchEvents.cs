namespace ValorantMatchTimer.Core;

public abstract record MatchEvent;

public sealed record MatchStartEvent(
    DateTimeOffset Start,
    string Map,
    string Broad,
    bool Resumed = false) : MatchEvent;

public sealed record MatchEndEvent(
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    string Map,
    string Broad) : MatchEvent;

public sealed record PhaseEvent(string Phase) : MatchEvent;

public sealed record StatusEvent(string Text) : MatchEvent;
