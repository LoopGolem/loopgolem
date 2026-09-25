namespace LoopGolem.Core.Domain;

public sealed record RecoveryPlannerContext(
    string FailedTaskId,
    string FailedDefinitionId,
    string FailedTaskTitle,
    PlannedTask FailedTaskDefinition,
    int Cycle,
    string FailureSummary,
    string FailureError,
    string? FailureEvidenceJson);

public sealed record RecoveryPlan(
    string Summary,
    IReadOnlyList<PlannedTask> Tasks);
