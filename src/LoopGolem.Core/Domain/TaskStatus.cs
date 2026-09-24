namespace LoopGolem.Core.Domain;

public enum TaskStatus
{
    Planned,
    Ready,
    Running,
    Verifying,
    Retrying,
    Escalated,
    Blocked,
    Failed,
    Completed
}
