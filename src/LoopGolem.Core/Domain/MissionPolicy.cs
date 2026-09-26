namespace LoopGolem.Core.Domain;

public enum SessionReuseMode
{
    Disabled,
    Affinity
}

public enum WorkerContextStrategy
{
    Legacy,
    Fresh,
    Affinity,
    SupervisorFork
}

public enum WorkerReasoningEffort
{
    Low,
    High
}

public sealed record MissionPolicy(
    int MaxRecoveryCycles,
    int MaxValidationCycles,
    SessionReuseMode SessionReuse)
{
    public int MaxWorkerSessionMicrotasks { get; init; } = 3;
    public int MaxWorkerSessionIdleMinutes { get; init; } = 30;
    public int MaxActiveWorkerSessions { get; init; } = 4;

    public WorkerContextStrategy WorkerContext { get; init; } =
        WorkerContextStrategy.Legacy;

    public WorkerReasoningEffort WorkerReasoning { get; init; } =
        WorkerReasoningEffort.Low;

    // Experimental benchmark controls. Normal missions leave both unset.
    public bool PauseAfterPlanning { get; init; }

    public string? FrozenPlannerResultJson { get; init; }

    public WorkerContextStrategy EffectiveWorkerContextStrategy =>
        WorkerContext == WorkerContextStrategy.Legacy
            ? SessionReuse == SessionReuseMode.Affinity
                ? WorkerContextStrategy.Affinity
                : WorkerContextStrategy.Fresh
            : WorkerContext;

    public string EffectiveWorkerReasoningEffort =>
        WorkerReasoning == WorkerReasoningEffort.High
            ? "high"
            : "low";

    public static MissionPolicy Default { get; } =
        new(
            MaxRecoveryCycles: 3,
            MaxValidationCycles: 3,
            SessionReuse: SessionReuseMode.Affinity);
}
