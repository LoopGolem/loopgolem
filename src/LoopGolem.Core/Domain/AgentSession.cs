namespace LoopGolem.Core.Domain;

public enum AgentSessionRole
{
    Supervisor,
    Worker,
    Validator
}

public enum AgentSessionStatus
{
    Active,
    Closed,
    Invalidated
}

public sealed record AgentSession(
    string Id,
    string MissionId,
    AgentSessionRole Role,
    string Model,
    string ReasoningEffort,
    string? ProviderThreadId,
    AgentSessionStatus Status,
    string? LeaseOwnerTaskId,
    int TurnCount,
    int MicrotaskCount,
    string? TerminationReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUsedAtUtc,
    DateTimeOffset UpdatedAtUtc);
