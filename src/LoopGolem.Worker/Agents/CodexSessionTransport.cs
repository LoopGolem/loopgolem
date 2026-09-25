namespace LoopGolem.Worker.Agents;

internal enum CodexSessionMode
{
    EphemeralFresh,
    NewPersistent,
    Resume
}

internal sealed record CodexSessionRequest(
    CodexSessionMode Mode,
    string? SessionId = null,
    string? ThreadId = null)
{
    public static CodexSessionRequest EphemeralFresh { get; } =
        new(CodexSessionMode.EphemeralFresh);

    public static CodexSessionRequest NewPersistent { get; } =
        new(CodexSessionMode.NewPersistent);

    public static CodexSessionRequest Resume(
        string sessionId,
        string threadId) =>
        new(
            CodexSessionMode.Resume,
            sessionId,
            threadId);
}
