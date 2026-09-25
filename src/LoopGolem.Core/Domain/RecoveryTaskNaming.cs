namespace LoopGolem.Core.Domain;

public static class RecoveryTaskNaming
{
    public static string GetRepairPrefix(
        string failedTaskId,
        int cycleNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            failedTaskId);

        return $"repair{cycleNumber}_{failedTaskId}_";
    }
}
