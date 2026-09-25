namespace LoopGolem.Core.Domain;

public sealed record TaskExecutionResult(
    bool Success,
    string Summary,
    string? Details = null,
    string? Error = null,
    TokenUsage? TokenUsage = null)
{
    public static TaskExecutionResult Succeeded(
        string summary,
        string? details = null,
        TokenUsage? tokenUsage = null) =>
        new(true, summary, details, null, tokenUsage);

    public static TaskExecutionResult Failed(
        string summary,
        string error,
        string? details = null,
        TokenUsage? tokenUsage = null) =>
        new(false, summary, details, error, tokenUsage);
}
