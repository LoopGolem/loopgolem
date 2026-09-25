namespace LoopGolem.Core.Domain;

public enum RecoveryCycleStatus
{
    Pending,
    Planning,
    Repairing,
    Retrying,
    Succeeded,
    Exhausted
}

public sealed record RecoveryCycle(
    string Id,
    string MissionId,
    string FailedTaskId,
    int CycleNumber,
    RecoveryCycleStatus Status,
    string? FailureAttemptId,
    string? RecoveryTurnId,
    IReadOnlyList<string> RepairTaskIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
