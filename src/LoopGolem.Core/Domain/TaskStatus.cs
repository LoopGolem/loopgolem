namespace LoopGolem.Core.Domain;

public enum TaskStatus
{
    Planned,
    Ready,
    Running,
    Verifying,
    Retrying,
    RecoveryPending,
    Escalated,
    Blocked,
    Failed,
    Completed
}
