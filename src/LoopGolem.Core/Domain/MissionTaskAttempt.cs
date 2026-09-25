namespace LoopGolem.Core.Domain;

public enum MissionTaskAttemptOutcome
{
    Running,
    Succeeded,
    Failed,
    Interrupted
}

public sealed record MissionTaskAttempt(
    string Id,
    string MissionId,
    string TaskId,
    int AttemptNumber,
    MissionTaskAttemptOutcome Outcome,
    string? Summary,
    string? Error,
    string? EvidenceJson,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);
