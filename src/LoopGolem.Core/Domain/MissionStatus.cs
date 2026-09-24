namespace LoopGolem.Core.Domain;

public enum MissionStatus
{
    Created,
    Planning,
    Running,
    WaitingForQuota,
    WaitingForApproval,
    Paused,
    Failed,
    Completed
}
