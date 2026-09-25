namespace LoopGolem.Core.Domain;

public sealed record TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens)
{
    public long CacheWriteInputTokens { get; init; }

    public TokenUsage Add(TokenUsage other) =>
        new(
            checked(InputTokens + other.InputTokens),
            checked(CachedInputTokens + other.CachedInputTokens),
            checked(OutputTokens + other.OutputTokens),
            checked(ReasoningOutputTokens + other.ReasoningOutputTokens),
            checked(TotalTokens + other.TotalTokens))
        {
            CacheWriteInputTokens = checked(
                CacheWriteInputTokens +
                other.CacheWriteInputTokens)
        };
}
