namespace LoopGolem.Core.Domain;

public sealed record TaskExecutionResult(
    bool Success,
    string Summary,
    string? Details = null,
    string? Error = null)
{
    public static TaskExecutionResult Succeeded(
        string summary,
        string? details = null) =>
        new(true, summary, details);

    public static TaskExecutionResult Failed(
        string summary,
        string error,
        string? details = null) =>
        new(false, summary, details, error);
}
