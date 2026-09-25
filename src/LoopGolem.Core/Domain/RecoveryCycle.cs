namespace LoopGolem.Core.Domain;

public enum RecoveryCycleStatus
{
    Pending,
    Planning,
    Repairing,
    Retrying,
    Succeeded,
    Failed,
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

public sealed record RecoveryPlan(
    string Summary,
    IReadOnlyList<PlannedTask> Tasks);

public sealed record RecoveryPlanningResult(
    bool Success,
    string Summary,
    IReadOnlyList<PlannedTask> Tasks,
    string? Error,
    string? RecoveryTurnId)
{
    public static RecoveryPlanningResult Succeeded(
        string summary,
        IReadOnlyList<PlannedTask> tasks,
        string? recoveryTurnId) =>
        new(
            true,
            summary,
            tasks,
            null,
            recoveryTurnId);

    public static RecoveryPlanningResult Failed(
        string summary,
        string error,
        string? recoveryTurnId = null) =>
        new(
            false,
            summary,
            [],
            error,
            recoveryTurnId);
}
