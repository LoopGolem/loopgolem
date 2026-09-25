namespace LoopGolem.Core.Domain;

public sealed record Mission(
    string Id,
    string Goal,
    string WorkspacePath,
    MissionExecutionMode ExecutionMode,
    MissionStatus Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public MissionPolicy Policy { get; init; } = MissionPolicy.Default;
}
