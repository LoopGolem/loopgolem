namespace LoopGolem.Core.Domain;

public static class RecoveryTaskNaming
{
    public static string GetRepairPrefix(
        string failedTaskId,
        int cycleNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            failedTaskId);

        var compactId =
            failedTaskId.Length <= 8
                ? failedTaskId
                : failedTaskId[..8];

        return $"repair{cycleNumber}_{compactId}_";
    }
}
