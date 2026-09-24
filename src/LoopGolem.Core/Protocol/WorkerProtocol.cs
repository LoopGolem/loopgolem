using LoopGolem.Core.Domain;

namespace LoopGolem.Core.Protocol;

public static class WorkerProtocol
{
    public const string PipeName = "loopgolem-worker-v1";
    public const string Ping = "ping";
    public const string GetCodexStatus = "getCodexStatus";
    public const string CreateMission = "createMission";
    public const string GetMission = "getMission";
}

public static class WorkerErrorCodes
{
    public const string EmptyRequest = "empty_request";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidJson = "invalid_json";
    public const string MissionGoalRequired = "mission_goal_required";
    public const string WorkspaceInvalid = "workspace_invalid";
    public const string MissionIdRequired = "mission_id_required";
    public const string MissionNotFound = "mission_not_found";
    public const string UnsupportedRequest = "unsupported_request";
    public const string InternalError = "internal_error";
}

public sealed record CodexRuntimeStatus(
    bool Available,
    bool ChatGptAuthenticated,
    string? Version,
    string Message);

public sealed record WorkerRequest(
    string Type,
    string? MissionId = null,
    string? Goal = null,
    string? WorkspacePath = null,
    MissionExecutionMode ExecutionMode = MissionExecutionMode.ValidateOnly);

public sealed record WorkerResponse(
    bool Success,
    string? Error = null,
    string? ErrorCode = null,
    string? WorkerStatus = null,
    MissionSnapshot? Mission = null,
    CodexRuntimeStatus? CodexStatus = null);
