namespace LoopGolem.Core.Domain;

public enum SessionReuseMode
{
    Disabled,
    Affinity
}

public sealed record MissionExecutionPolicy(
    int MaxDeterministicRecoveryCycles = 3,
    int MaxValidationCycles = 3,
    SessionReuseMode SessionReuse = SessionReuseMode.Disabled)
{
    public static MissionExecutionPolicy Default { get; } = new();
}
