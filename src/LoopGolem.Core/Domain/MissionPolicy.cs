namespace LoopGolem.Core.Domain;

public enum SessionReuseMode
{
    Disabled,
    Affinity
}

public enum WorkerContextStrategy
{
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

    /// <summary>
    /// Optional explicit Worker context policy. Null preserves the legacy
    /// SessionReuse behavior for persisted missions created before this field
    /// existed.
    /// </summary>
    public WorkerContextStrategy? WorkerContext { get; init; }

    /// <summary>
    /// Worker reasoning policy. Existing missions deserialize to Low.
    /// </summary>
    public WorkerReasoningEffort WorkerReasoning { get; init; } =
        WorkerReasoningEffort.Low;

    public WorkerContextStrategy EffectiveWorkerContext =>
        WorkerContext ??
        (SessionReuse == SessionReuseMode.Affinity
            ? WorkerContextStrategy.Affinity
            : WorkerContextStrategy.Fresh);

    public static MissionPolicy Default { get; } =
        new(
            MaxRecoveryCycles: 3,
            MaxValidationCycles: 3,
            SessionReuse: SessionReuseMode.Affinity)
        {
            WorkerContext = WorkerContextStrategy.Affinity,
            WorkerReasoning = WorkerReasoningEffort.Low
        };
}
