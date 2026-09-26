using LoopGolem.Core.Domain;

namespace LoopGolem.Core.Protocol;

public static class WorkerProtocol
{
    public const string PipeName = "loopgolem-worker-v1";
    public const string Ping = "ping";
    public const string GetCodexStatus = "getCodexStatus";
    public const string GetCodexAllowance = "getCodexAllowance";
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

public enum CodexRuntimeState
{
    Ready,
    WslDistributionMissing,
    CodexCliMissing,
    AuthenticationRequired,
    Unavailable
}

public sealed record CodexRuntimeStatus(
    bool Available,
    bool ChatGptAuthenticated,
    string? Version,
    string Message,
    CodexRuntimeState State = CodexRuntimeState.Unavailable,
    string Runtime = "native",
    string? Distribution = null,
    string? Model = null);

public sealed record CodexAllowanceSnapshot(
    DateTimeOffset ObservedAtUtc,
    double? PrimaryUsedPercent,
    int? PrimaryWindowMinutes,
    long? PrimaryResetsAtUnix,
    double? SecondaryUsedPercent,
    int? SecondaryWindowMinutes,
    long? SecondaryResetsAtUnix,
    bool? OrdinaryUsageAllowed,
    string? LimitId,
    string? LimitName);

public sealed record AgentRoleTelemetrySummary(
    AgentSessionRole Role,
    int Sessions,
    int Turns,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens);

public sealed record MissionTelemetrySummary(
    long DurationMilliseconds,
    int Sessions,
    int ActiveSessions,
    int InvalidatedSessions,
    int Turns,
    int RecoveryCycles,
    int SuccessfulRecoveryCycles,
    int ExhaustedRecoveryCycles,
    int WorkerReusedTurns,
    int WorkerReuseRecommendedTurns,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    IReadOnlyList<AgentRoleTelemetrySummary> Roles);

public sealed record WorkerRequest(
    string Type,
    string? MissionId = null,
    string? Goal = null,
    string? WorkspacePath = null,
    MissionExecutionMode ExecutionMode = MissionExecutionMode.Codex,
    SessionReuseMode? SessionReuse = null,
    WorkerContextStrategy? WorkerContext = null,
    WorkerReasoningEffort? WorkerReasoning = null,
    bool PauseAfterPlanning = false,
    string? FrozenPlannerResultJson = null);

public sealed record WorkerResponse(
    bool Success,
    string? Error = null,
    string? ErrorCode = null,
    string? WorkerStatus = null,
    MissionSnapshot? Mission = null,
    CodexRuntimeStatus? CodexStatus = null,
    MissionTelemetrySummary? Telemetry = null,
    CodexAllowanceSnapshot? CodexAllowance = null);
