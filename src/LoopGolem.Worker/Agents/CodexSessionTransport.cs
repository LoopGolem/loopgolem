using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public enum CodexSessionMode
{
    FreshEphemeral,
    NewPersistent,
    Resume
}

public sealed record CodexStructuredRunRequest(
    string MissionId,
    string? TaskId,
    AgentSessionRole Role,
    AgentTurnPurpose Purpose,
    CodexSessionMode SessionMode,
    string? SessionId,
    string Workspace,
    string Model,
    string ReasoningEffort,
    string Sandbox,
    string Schema,
    string Prompt)
{
    public string? LeaseOwnerTaskId { get; init; }
}

public sealed record CodexStructuredRunResult(
    ProcessRunResult Process,
    string FinalMessage,
    string Details,
    TokenUsage? TokenUsage,
    string SessionId,
    string? ProviderThreadId,
    int TurnNumber,
    string TurnId);

public interface ICodexSessionTransport
{
    Task<CodexStructuredRunResult> RunStructuredAsync(
        CodexStructuredRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class CodexSessionTransport(
    ProcessRunner processRunner,
    CodexCliService runtime,
    IMissionStore store) : ICodexSessionTransport
{
    private static readonly TimeSpan ExecutionTimeout =
        TimeSpan.FromMinutes(60);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WslRuntimeService _wsl = new(processRunner);

    public async Task<CodexStructuredRunResult> RunStructuredAsync(
        CodexStructuredRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MissionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReasoningEffort);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sandbox);

        var session = await ResolveSessionAsync(
            request,
            cancellationToken);

        var turnNumber =
            await AllocateTurnNumberAsync(
                request.MissionId,
                session,
                cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        var turn = new AgentTurn(
            Guid.NewGuid().ToString("N"),
            request.MissionId,
            request.TaskId,
            session.Id,
            request.Purpose,
            request.Model,
            request.ReasoningEffort,
            turnNumber,
            startedAt,
            null,
            request.LeaseOwnerTaskId,
            0,
            0,
            0,
            0,
            0,
            0);

        await store.UpsertAgentTurnAsync(
            turn,
            cancellationToken);

        var runtimeDirectory = Path.Combine(
            Path.GetTempPath(),
            "LoopGolem",
            "structured",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDirectory);

        var outputPath = Path.Combine(
            runtimeDirectory,
            "last-message.json");
        var schemaPath = Path.Combine(
            runtimeDirectory,
            "schema.json");

        await File.WriteAllTextAsync(
            schemaPath,
            request.Schema,
            cancellationToken);

        string? observedThreadId = null;
        TokenUsage? latestUsage = null;

        async Task HandleOutputLineAsync(string line)
        {
            if (TryParseThreadStarted(
                    line,
                    out var threadId))
            {
                observedThreadId = threadId;
                session =
                    await CaptureThreadStartedAsync(
                        request.SessionMode,
                        session,
                        threadId!);
            }

            if (TryParseTokenUsageLine(
                    line,
                    out var usage))
            {
                latestUsage = usage;
            }
        }

        try
        {
            ProcessRunResult process;

            if (OperatingSystem.IsWindows())
            {
                var status =
                    await runtime.GetStatusAsync(
                        cancellationToken);
                var distribution = status.Distribution
                    ?? throw new InvalidOperationException(
                        "WSL distribution is unavailable.");

                var wslWorkspace =
                    await _wsl.ConvertWindowsPathAsync(
                        distribution,
                        request.Workspace,
                        cancellationToken);
                var wslOutput =
                    await _wsl.ConvertWindowsPathAsync(
                        distribution,
                        outputPath,
                        cancellationToken);
                var wslSchema =
                    await _wsl.ConvertWindowsPathAsync(
                        distribution,
                        schemaPath,
                        cancellationToken);

                process =
                    await _wsl.RunLoginShellExecutableStreamingAsync(
                        distribution,
                        "codex",
                        BuildArguments(
                            request,
                            wslWorkspace,
                            wslSchema,
                            wslOutput,
                            session.ProviderThreadId),
                        ExecutionTimeout,
                        cancellationToken,
                        request.Prompt,
                        HandleOutputLineAsync);
            }
            else
            {
                process =
                    await processRunner.RunStreamingAsync(
                        "codex",
                        BuildArguments(
                            request,
                            request.Workspace,
                            schemaPath,
                            outputPath,
                            session.ProviderThreadId),
                        request.Workspace,
                        ExecutionTimeout,
                        cancellationToken,
                        request.Prompt,
                        null,
                        HandleOutputLineAsync);
            }

            latestUsage ??=
                ParseTokenUsage(process.StandardOutput);

            var completedAt = DateTimeOffset.UtcNow;
            turn = turn with
            {
                CompletedAtUtc = completedAt,
                DurationMilliseconds =
                    process.DurationMilliseconds,
                InputTokens =
                    latestUsage?.InputTokens ?? 0,
                CachedInputTokens =
                    latestUsage?.CachedInputTokens ?? 0,
                CacheWriteInputTokens =
                    latestUsage?.CacheWriteInputTokens ?? 0,
                OutputTokens =
                    latestUsage?.OutputTokens ?? 0,
                ReasoningOutputTokens =
                    latestUsage?.ReasoningOutputTokens ?? 0,
                TotalTokens =
                    latestUsage?.TotalTokens ?? 0
            };

            await store.UpsertAgentTurnAsync(
                turn,
                CancellationToken.None);

            var statusAfterRun =
                request.SessionMode ==
                    CodexSessionMode.FreshEphemeral
                    ? AgentSessionStatus.Closed
                    : session.Status;
            string? terminationReason =
                request.SessionMode ==
                    CodexSessionMode.FreshEphemeral
                    ? "ephemeral"
                    : session.TerminationReason;

            if (request.SessionMode ==
                    CodexSessionMode.NewPersistent &&
                string.IsNullOrWhiteSpace(
                    session.ProviderThreadId))
            {
                statusAfterRun =
                    AgentSessionStatus.Invalidated;
                terminationReason =
                    "Codex did not emit thread.started.";
            }

            session = session with
            {
                Status = statusAfterRun,
                TurnCount = turnNumber,
                LastUsedAtUtc = completedAt,
                UpdatedAtUtc = completedAt,
                TerminationReason = terminationReason
            };

            await store.UpsertAgentSessionAsync(
                session,
                CancellationToken.None);

            var finalMessage =
                File.Exists(outputPath)
                    ? await File.ReadAllTextAsync(
                        outputPath,
                        cancellationToken)
                    : string.Empty;

            var resumableThreadId =
                request.SessionMode ==
                    CodexSessionMode.FreshEphemeral
                    ? null
                    : session.ProviderThreadId ??
                        observedThreadId;

            var details = JsonSerializer.Serialize(
                new
                {
                    sessionMode =
                        request.SessionMode.ToString(),
                    sessionId = session.Id,
                    providerThreadId =
                        resumableThreadId,
                    observedThreadId,
                    turnNumber,
                    model = request.Model,
                    reasoning = request.ReasoningEffort,
                    sandbox = request.Sandbox,
                    tokenUsage = latestUsage,
                    process
                },
                JsonOptions);

            return new CodexStructuredRunResult(
                process,
                finalMessage.Trim(),
                details,
                latestUsage,
                session.Id,
                resumableThreadId,
                turnNumber,
                turn.Id);
        }
        finally
        {
            try
            {
                Directory.Delete(
                    runtimeDirectory,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    private async Task<int> AllocateTurnNumberAsync(
        string missionId,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var turns =
            await store.ListAgentTurnsAsync(
                missionId,
                cancellationToken);
        return GetNextTurnNumber(
            session,
            turns);
    }

    internal static int GetNextTurnNumber(
        AgentSession session,
        IEnumerable<AgentTurn> turns)
    {
        var highestPersisted =
            turns
                .Where(turn =>
                    string.Equals(
                        turn.SessionId,
                        session.Id,
                        StringComparison.Ordinal))
                .Select(turn => turn.TurnNumber)
                .DefaultIfEmpty(session.TurnCount)
                .Max();

        return checked(
            Math.Max(
                session.TurnCount,
                highestPersisted) + 1);
    }

    internal async Task<AgentSession>
        CaptureThreadStartedAsync(
            CodexSessionMode sessionMode,
            AgentSession session,
            string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            threadId);

        if (sessionMode ==
                CodexSessionMode.Resume &&
            !string.Equals(
                session.ProviderThreadId,
                threadId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Codex resumed thread '{threadId}', but LoopGolem expected '{session.ProviderThreadId}'.");
        }

        if (sessionMode ==
                CodexSessionMode.NewPersistent &&
            !string.IsNullOrWhiteSpace(
                session.ProviderThreadId) &&
            !string.Equals(
                session.ProviderThreadId,
                threadId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Codex emitted a second thread id '{threadId}' after '{session.ProviderThreadId}' was already captured.");
        }

        if (sessionMode !=
            CodexSessionMode.NewPersistent)
        {
            return session;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = session with
        {
            ProviderThreadId = threadId,
            LastUsedAtUtc = now,
            UpdatedAtUtc = now
        };

        // Deliberately do not use the caller token here. Once Codex has
        // announced a persistent thread id, recording it is part of
        // crash-safe state capture even if cancellation is requested
        // immediately afterwards.
        await store.UpsertAgentSessionAsync(
            updated,
            CancellationToken.None);

        return updated;
    }

    private async Task<AgentSession> ResolveSessionAsync(
        CodexStructuredRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionMode ==
            CodexSessionMode.Resume)
        {
            if (string.IsNullOrWhiteSpace(
                    request.SessionId))
            {
                throw new InvalidOperationException(
                    "A LoopGolem session id is required to resume a Codex thread.");
            }

            var sessions =
                await store.ListAgentSessionsAsync(
                    request.MissionId,
                    cancellationToken);
            var existing =
                sessions.FirstOrDefault(
                    candidate =>
                        string.Equals(
                            candidate.Id,
                            request.SessionId,
                            StringComparison.Ordinal));

            if (existing is null)
            {
                throw new InvalidOperationException(
                    $"LoopGolem session '{request.SessionId}' was not found.");
            }

            if (existing.Status !=
                    AgentSessionStatus.Active ||
                string.IsNullOrWhiteSpace(
                    existing.ProviderThreadId))
            {
                throw new InvalidOperationException(
                    $"LoopGolem session '{existing.Id}' is not resumable.");
            }

            if (existing.Role != request.Role ||
                !string.Equals(
                    existing.Model,
                    request.Model,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    existing.ReasoningEffort,
                    request.ReasoningEffort,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"LoopGolem session '{existing.Id}' does not match the requested role/model/reasoning policy.");
            }

            return existing;
        }

        if (!string.IsNullOrWhiteSpace(
                request.SessionId))
        {
            throw new InvalidOperationException(
                "A session id may only be supplied in Resume mode.");
        }

        var now = DateTimeOffset.UtcNow;
        var isEphemeral =
            request.SessionMode ==
            CodexSessionMode.FreshEphemeral;
        var session = new AgentSession(
            Guid.NewGuid().ToString("N"),
            request.MissionId,
            request.Role,
            request.Model,
            request.ReasoningEffort,
            null,
            isEphemeral
                ? AgentSessionStatus.Closed
                : AgentSessionStatus.Active,
            null,
            0,
            0,
            isEphemeral
                ? "ephemeral"
                : null,
            now,
            now,
            now);

        await store.UpsertAgentSessionAsync(
            session,
            cancellationToken);

        return session;
    }

    internal static IReadOnlyList<string> BuildArguments(
        CodexStructuredRunRequest request,
        string workspace,
        string schemaPath,
        string outputPath,
        string? providerThreadId)
    {
        if (request.SessionMode ==
                CodexSessionMode.Resume &&
            string.IsNullOrWhiteSpace(
                providerThreadId))
        {
            throw new InvalidOperationException(
                "A provider thread id is required in Resume mode.");
        }

        var arguments = new List<string>
        {
            "exec",
            "--json"
        };

        if (request.SessionMode ==
            CodexSessionMode.FreshEphemeral)
        {
            arguments.Add("--ephemeral");
        }

        arguments.AddRange(
        [
            "--ignore-user-config",
            "--disable", "apps",
            "--disable", "plugins",
            "--disable", "multi_agent",
            "--disable", "memories",
            "--color", "never",
            "--sandbox", request.Sandbox,
            "--cd", workspace,
            "--model", request.Model,
            "--config",
                $"model_reasoning_effort=\"{request.ReasoningEffort}\"",
            "--config", "approval_policy=never",
            "--config",
                "sandbox_workspace_write.network_access=false",
            "--output-schema", schemaPath,
            "--output-last-message", outputPath
        ]);

        if (request.SessionMode ==
            CodexSessionMode.Resume)
        {
            arguments.Add("resume");
            arguments.Add(providerThreadId!);
        }

        arguments.Add("-");
        return arguments;
    }

    internal static bool TryParseThreadStarted(
        string line,
        out string? threadId)
    {
        threadId = null;

        try
        {
            using var document =
                JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty(
                    "type",
                    out var type) ||
                type.ValueKind !=
                    JsonValueKind.String ||
                !string.Equals(
                    type.GetString(),
                    "thread.started",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty(
                    "thread_id",
                    out var id) ||
                id.ValueKind !=
                    JsonValueKind.String)
            {
                return false;
            }

            threadId = id.GetString();
            return !string.IsNullOrWhiteSpace(
                threadId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static TokenUsage? ParseTokenUsage(
        string jsonLines)
    {
        TokenUsage? latest = null;

        foreach (var line in jsonLines.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            if (TryParseTokenUsageLine(
                    line,
                    out var usage))
            {
                latest = usage;
            }
        }

        return latest;
    }

    internal static bool TryParseTokenUsageLine(
        string line,
        out TokenUsage? usage)
    {
        usage = null;

        try
        {
            using var document =
                JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!TryGetUsageElement(
                    root,
                    out var usageElement))
            {
                return false;
            }

            usage = ParseUsageElement(
                usageElement);
            return true;
        }
        catch (JsonException)
        {
            // Ignore non-JSON diagnostics. The structured Codex stream may
            // coexist with launcher output on some runtimes.
            return false;
        }
    }

    private static bool TryGetUsageElement(
        JsonElement root,
        out JsonElement usage)
    {
        if (root.TryGetProperty(
                "type",
                out var type) &&
            type.ValueKind ==
                JsonValueKind.String &&
            string.Equals(
                type.GetString(),
                "turn.completed",
                StringComparison.Ordinal) &&
            root.TryGetProperty(
                "usage",
                out usage) &&
            usage.ValueKind ==
                JsonValueKind.Object)
        {
            return true;
        }

        if (root.TryGetProperty(
                "msg",
                out var message) &&
            TryGetLegacyTokenUsage(
                message,
                out usage))
        {
            return true;
        }

        if (root.TryGetProperty(
                "payload",
                out var payload) &&
            TryGetLegacyTokenUsage(
                payload,
                out usage))
        {
            return true;
        }

        usage = default;
        return false;
    }

    private static bool TryGetLegacyTokenUsage(
        JsonElement container,
        out JsonElement usage)
    {
        if (container.ValueKind ==
                JsonValueKind.Object &&
            container.TryGetProperty(
                "type",
                out var type) &&
            type.ValueKind ==
                JsonValueKind.String &&
            string.Equals(
                type.GetString(),
                "token_count",
                StringComparison.Ordinal) &&
            container.TryGetProperty(
                "info",
                out var info) &&
            info.ValueKind ==
                JsonValueKind.Object)
        {
            if (info.TryGetProperty(
                    "total_token_usage",
                    out usage) &&
                usage.ValueKind ==
                    JsonValueKind.Object)
            {
                return true;
            }

            if (info.TryGetProperty(
                    "last_token_usage",
                    out usage) &&
                usage.ValueKind ==
                    JsonValueKind.Object)
            {
                return true;
            }
        }

        usage = default;
        return false;
    }

    private static TokenUsage ParseUsageElement(
        JsonElement usage)
    {
        var input =
            GetTokenCount(
                usage,
                "input_tokens");
        var cachedInput =
            GetTokenCount(
                usage,
                "cached_input_tokens");
        var cacheWriteInput =
            GetTokenCount(
                usage,
                "cache_write_input_tokens");
        var output =
            GetTokenCount(
                usage,
                "output_tokens");
        var reasoningOutput =
            GetTokenCount(
                usage,
                "reasoning_output_tokens");
        var total =
            GetTokenCount(
                usage,
                "total_tokens");

        if (total == 0)
        {
            total = checked(input + output);
        }

        return new TokenUsage(
            input,
            cachedInput,
            output,
            reasoningOutput,
            total)
        {
            CacheWriteInputTokens =
                cacheWriteInput
        };
    }

    private static long GetTokenCount(
        JsonElement usage,
        string propertyName) =>
        usage.TryGetProperty(
            propertyName,
            out var value) &&
        value.ValueKind ==
            JsonValueKind.Number &&
        value.TryGetInt64(
            out var count)
            ? count
            : 0;
}
