namespace LoopGolem.Core.Domain;

public enum TaskAttemptOutcome
{
    Running,
    Succeeded,
    Failed,
    Interrupted
}

public enum TaskFailureKind
{
    None,
    DeterministicCheck,
    Infrastructure,
    UnsafeInterruptedSideEffect,
    Agent,
    Validation,
    Unknown
}

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

public enum AgentTurnPurpose
{
    Planning,
    Work,
    Recovery,
    Validation
}

public enum RecoveryEpisodeStatus
{
    Planning,
    Repairing,
    Retrying,
    Succeeded,
    Exhausted,
    Failed
}

public sealed record MissionTaskAttempt(
    string Id,
    string MissionId,
    string TaskId,
    int AttemptNumber,
    TaskAttemptOutcome Outcome,
    TaskFailureKind FailureKind,
    string? Summary,
    string? EvidenceJson,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record AgentSession(
    string Id,
    string MissionId,
    AgentSessionRole Role,
    string Provider,
    string? ThreadId,
    string Model,
    string ReasoningEffort,
    AgentSessionStatus Status,
    string? LeaseTaskId,
    int TurnCount,
    int MicrotaskCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUsedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string? TerminationReason);

public sealed record AgentTurn(
    string Id,
    string MissionId,
    string SessionId,
    string? TaskId,
    AgentTurnPurpose Purpose,
    int TurnNumber,
    string Model,
    string ReasoningEffort,
    TokenUsage? TokenUsage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMilliseconds,
    bool? Success,
    string? Error);

public sealed record RecoveryEpisode(
    string Id,
    string MissionId,
    string FailedTaskId,
    int Cycle,
    RecoveryEpisodeStatus Status,
    string? FailedAttemptId,
    string? RecoveryTurnId,
    IReadOnlyList<string> RepairTaskIds,
    string? FailureEvidenceJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
