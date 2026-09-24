namespace LoopGolem.Worker.Infrastructure;

public static class DevelopmentDiagnostics
{
    public static bool Enabled
    {
        get
        {
#if DEBUG
            return true;
#else
            return string.Equals(
                Environment.GetEnvironmentVariable(
                    "LOOPGOLEM_DEV_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal);
#endif
        }
    }

    public static void Write(
        string channel,
        string missionId,
        string? payload)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        Console.WriteLine(
            $"[LoopGolem diagnostics] {channel} mission={missionId}");
        Console.WriteLine(payload);
        Console.WriteLine("[/LoopGolem diagnostics]");
    }
}
