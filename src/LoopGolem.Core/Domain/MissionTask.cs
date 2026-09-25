namespace LoopGolem.Core.Domain;

public sealed record MissionTask(
    string Id,
    string MissionId,
    int Sequence,
    MissionTaskKind Kind,
    string Title,
    PlannedTask? Definition,
    TaskStatus Status,
    string? Result,
    string? ResultDetails,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public string? ExecutionContext { get; init; }
    public int ExecutionAttemptCount { get; init; } = 0;
}
