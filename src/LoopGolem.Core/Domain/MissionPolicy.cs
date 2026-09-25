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
    public int MaxWorkerSessionMicrotasks { get; init; } = 3;
    public int MaxWorkerSessionIdleMinutes { get; init; } = 30;
    public int MaxActiveWorkerSessions { get; init; } = 4;

    public static MissionPolicy Default { get; } =
        new(
            MaxRecoveryCycles: 3,
            MaxValidationCycles: 3,
            SessionReuse: SessionReuseMode.Affinity);
}
