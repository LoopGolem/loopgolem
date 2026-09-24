namespace LoopGolem.Core.Domain;

public sealed record MissionSnapshot(
    Mission Mission,
    IReadOnlyList<MissionTask> Tasks);
