namespace LoopGolem.Core.Domain;

public enum SessionReuseMode
{
    Disabled,
    Affinity
}

public sealed record MissionPolicy(
    int MaxRecoveryCycles,
    int MaxValidationCycles,
    SessionReuseMode SessionReuse)
{
    public static MissionPolicy Default { get; } =
        new(
            MaxRecoveryCycles: 3,
            MaxValidationCycles: 3,
            SessionReuse: SessionReuseMode.Disabled);
}
