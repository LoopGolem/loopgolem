namespace LoopGolem.Core.Domain;

public enum TaskFailureKind
{
    None,
    KnownDeterministicFailure,
    InfrastructureFailure,
    AgentFailure,
    UnknownFailure
}

public sealed record TaskExecutionResult(
    bool Success,
    string Summary,
    string? Details = null,
    string? Error = null,
    TokenUsage? TokenUsage = null,
    TaskFailureKind FailureKind = TaskFailureKind.None)
{
    public static TaskExecutionResult Succeeded(
        string summary,
        string? details = null,
        TokenUsage? tokenUsage = null) =>
        new(
            true,
            summary,
            details,
            null,
            tokenUsage,
            TaskFailureKind.None);

    public static TaskExecutionResult Failed(
        string summary,
        string error,
        string? details = null,
        TokenUsage? tokenUsage = null,
        TaskFailureKind failureKind =
            TaskFailureKind.UnknownFailure) =>
        new(
            false,
            summary,
            details,
            error,
            tokenUsage,
            failureKind);
}
