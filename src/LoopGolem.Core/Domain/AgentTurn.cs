namespace LoopGolem.Core.Domain;

public enum AgentTurnPurpose
{
    Planning,
    Work,
    Recovery,
    Validation
}

public sealed record AgentTurn(
    string Id,
    string MissionId,
    string? TaskId,
    string SessionId,
    AgentTurnPurpose Purpose,
    string Model,
    string ReasoningEffort,
    int TurnNumber,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMilliseconds,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens);
