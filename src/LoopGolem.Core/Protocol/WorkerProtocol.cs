using LoopGolem.Core.Domain;

namespace LoopGolem.Core.Protocol;

public static class WorkerProtocol
{
    public const string PipeName = "loopgolem-worker-v1";

    public const string Ping = "ping";
    public const string CreateMission = "createMission";
    public const string GetMission = "getMission";
}

public sealed record WorkerRequest(
    string Type,
    string? MissionId = null,
    string? Goal = null,
    string? WorkspacePath = null);

public sealed record WorkerResponse(
    bool Success,
    string? Error = null,
    string? WorkerStatus = null,
    MissionSnapshot? Mission = null);
