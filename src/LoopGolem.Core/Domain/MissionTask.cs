namespace LoopGolem.Core.Domain;

public sealed record MissionTask(
    string Id,
    string MissionId,
    int Sequence,
    string Title,
    TaskStatus Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
