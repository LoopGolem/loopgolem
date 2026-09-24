namespace LoopGolem.Core.Domain;

public sealed record Mission(
    string Id,
    string Goal,
    string WorkspacePath,
    MissionStatus Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
