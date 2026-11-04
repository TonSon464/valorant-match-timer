namespace ValorantMatchTimer.Core;

public sealed record OverlaySnapshot(
    DateTimeOffset Now,
    DateTimeOffset? Cutoff,
    string? MatchLabel,
    DateTimeOffset? MatchEnd,
    double MatchRemainingSeconds,
    bool InOverTime,
    string? MatchMap,
    bool PendingShutdown);
