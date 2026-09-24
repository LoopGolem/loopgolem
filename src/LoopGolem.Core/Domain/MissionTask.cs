namespace LoopGolem.Core.Domain;

public sealed record MissionTask(
    string Id,
    string MissionId,
    int Sequence,
    MissionTaskKind Kind,
    string Title,
    TaskStatus Status,
    string? Result,
    string? ResultDetails,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
