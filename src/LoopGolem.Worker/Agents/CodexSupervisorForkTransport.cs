using System.Diagnostics;
using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public interface ICodexSupervisorForkTransport
{
    Task<CodexStructuredRunResult> RunForkedWorkerAsync(
        Mission mission,
        MissionTask task,
        string parentProviderThreadId,
        string model,
        string reasoningEffort,
        string schema,
        string prompt,
        CancellationToken cancellationToken = default);
}

public sealed class CodexSupervisorForkTransport(
    ProcessRunner processRunner,
    CodexCliService runtime,
    IMissionStore store) : ICodexSupervisorForkTransport
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WslRuntimeService _wsl = new(processRunner);

    public async Task<CodexStructuredRunResult> RunForkedWorkerAsync(
        Mission mission,
        MissionTask task,
        string parentProviderThreadId,
        string model,
        string reasoningEffort,
        string schema,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentProviderThreadId);

        var runtimeStatus =
            await runtime.GetStatusAsync(cancellationToken);

        var workspace = mission.WorkspacePath;
        string appWorkspace;

        ProcessStartInfo startInfo;

        if (OperatingSystem.IsWindows())
        {
            var distribution = runtimeStatus.Distribution
                ?? throw new InvalidOperationException(
                    "WSL distribution is unavailable.");

            appWorkspace =
                await _wsl.ConvertWindowsPathAsync(
                    distribution,
                    workspace,
                    cancellationToken);

            startInfo = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(distribution);
            startInfo.ArgumentList.Add("--exec");
            startInfo.ArgumentList.Add("bash");
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(BuildAppServerCommand());
        }
        else
        {
            appWorkspace = workspace;
            startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workspace
            };

            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(BuildAppServerCommand());
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Could not start Codex app-server.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(
            cancellationToken);

        var rpc = new JsonRpcPipe(
            process.StandardInput,
            process.StandardOutput);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await rpc.RequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "loopgolem",
                        title = "LoopGolem",
                        version = "0.1"
                    },
                    capabilities = new
                    {
                        experimentalApi = false,
                        requestAttestation = false
                    }
                },
                cancellationToken);

            await rpc.NotifyAsync(
                "initialized",
                null,
                cancellationToken);

            var fork =
                await rpc.RequestAsync(
                    "thread/fork",
                    new
                    {
                        threadId = parentProviderThreadId,
                        model,
                        cwd = appWorkspace,
                        approvalPolicy = "never",
                        sandbox = "workspace-write",
                        config =
                            new Dictionary<string, object?>
                            {
                                ["model_reasoning_effort"] = "high",
                                ["sandbox_workspace_write.network_access"] = false
                            },
                        ephemeral = true,
                        excludeTurns = true
                    },
                    cancellationToken);

            var childThreadId =
                fork.GetProperty("thread")
                    .GetProperty("id")
                    .GetString()
                ?? throw new InvalidDataException(
                    "thread/fork returned no child thread id.");

            var forkedModel =
                fork.TryGetProperty(
                    "model",
                    out var forkModel)
                    ? forkModel.GetString()
                    : null;

            if (!string.Equals(
                    forkedModel,
                    model,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Forked Worker model is '{forkedModel ?? "(null)"}' instead of '{model}'.");
            }

            var inheritedEffort =
                fork.TryGetProperty(
                    "reasoningEffort",
                    out var forkEffort)
                    ? forkEffort.GetString()
                    : null;

            if (!string.Equals(
                    inheritedEffort,
                    "high",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Forked Worker did not inherit HIGH reasoning; observed '{inheritedEffort ?? "(null)"}'.");
            }

            var now = DateTimeOffset.UtcNow;
            var session = new AgentSession(
                Guid.NewGuid().ToString("N"),
                mission.Id,
                AgentSessionRole.Worker,
                model,
                reasoningEffort,
                childThreadId,
                AgentSessionStatus.Active,
                task.Id,
                0,
                0,
                null,
                now,
                now,
                now);

            await store.UpsertAgentSessionAsync(
                session,
                cancellationToken);

            using var schemaDocument =
                JsonDocument.Parse(schema);
            var outputSchema =
                schemaDocument.RootElement.Clone();

            var turnStart =
                await rpc.RequestAsync(
                    "turn/start",
                    new
                    {
                        threadId = childThreadId,
                        input = new[]
                        {
                            new
                            {
                                type = "text",
                                text = prompt,
                                text_elements =
                                    Array.Empty<object>()
                            }
                        },
                        effort = reasoningEffort,
                        outputSchema
                    },
                    cancellationToken);

            var providerTurnId =
                turnStart.GetProperty("turn")
                    .GetProperty("id")
                    .GetString()
                ?? throw new InvalidDataException(
                    "turn/start returned no turn id.");

            var logicalTurnId =
                Guid.NewGuid().ToString("N");
            var logicalTurn = new AgentTurn(
                logicalTurnId,
                mission.Id,
                task.Id,
                session.Id,
                AgentTurnPurpose.Work,
                model,
                reasoningEffort,
                1,
                now,
                null,
                null,
                0,
                0,
                0,
                0,
                0,
                0);

            await store.UpsertAgentTurnAsync(
                logicalTurn,
                cancellationToken);

            JsonElement? usageLast = null;
            JsonElement? completedTurn = null;

            while (completedTurn is null)
            {
                var message =
                    await rpc.ReadNextAsync(
                        cancellationToken);

                if (message.TryGetProperty(
                        "method",
                        out var methodElement))
                {
                    var method =
                        methodElement.GetString();

                    if (string.Equals(
                            method,
                            "thread/tokenUsage/updated",
                            StringComparison.Ordinal))
                    {
                        var parameters =
                            message.GetProperty("params");

                        if (parameters.TryGetProperty(
                                "turnId",
                                out var usageTurnId) &&
                            string.Equals(
                                usageTurnId.GetString(),
                                providerTurnId,
                                StringComparison.Ordinal))
                        {
                            usageLast =
                                parameters
                                    .GetProperty("tokenUsage")
                                    .GetProperty("last")
                                    .Clone();
                        }
                    }
                    else if (string.Equals(
                                 method,
                                 "turn/completed",
                                 StringComparison.Ordinal))
                    {
                        var turn =
                            message.GetProperty("params")
                                .GetProperty("turn");

                        if (string.Equals(
                                turn.GetProperty("id")
                                    .GetString(),
                                providerTurnId,
                                StringComparison.Ordinal))
                        {
                            completedTurn =
                                turn.Clone();
                        }
                    }
                }
            }

            var finalMessage =
                ExtractFinalAgentMessage(
                    completedTurn.Value);

            var usage =
                usageLast is { } last
                    ? ParseUsage(last)
                    : null;

            var completedAt =
                DateTimeOffset.UtcNow;

            logicalTurn =
                logicalTurn with
                {
                    CompletedAtUtc = completedAt,
                    DurationMilliseconds =
                        completedTurn.Value
                            .TryGetProperty(
                                "durationMs",
                                out var duration) &&
                        duration.ValueKind ==
                            JsonValueKind.Number
                            ? duration.GetInt64()
                            : stopwatch.ElapsedMilliseconds,
                    InputTokens =
                        usage?.InputTokens ?? 0,
                    CachedInputTokens =
                        usage?.CachedInputTokens ?? 0,
                    CacheWriteInputTokens =
                        usage?.CacheWriteInputTokens ?? 0,
                    OutputTokens =
                        usage?.OutputTokens ?? 0,
                    ReasoningOutputTokens =
                        usage?.ReasoningOutputTokens ?? 0,
                    TotalTokens =
                        usage?.TotalTokens ?? 0
                };

            await store.UpsertAgentTurnAsync(
                logicalTurn,
                CancellationToken.None);

            var read =
                await rpc.RequestAsync(
                    "thread/read",
                    new
                    {
                        threadId = childThreadId,
                        includeTurns = false
                    },
                    cancellationToken);

            var finalEffort =
                read.GetProperty("thread")
                    .GetProperty("reasoningEffort")
                    .GetString();

            if (!string.Equals(
                    finalEffort,
                    reasoningEffort,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Forked Worker ended at effort '{finalEffort ?? "(null)"}' instead of '{reasoningEffort}'.");
            }

            session = session with
            {
                Status = AgentSessionStatus.Closed,
                LeaseOwnerTaskId = null,
                TurnCount = 1,
                MicrotaskCount = 0,
                TerminationReason = "supervisor_fork_child",
                LastUsedAtUtc = completedAt,
                UpdatedAtUtc = completedAt
            };

            await store.UpsertAgentSessionAsync(
                session,
                CancellationToken.None);

            stopwatch.Stop();

            var details =
                JsonSerializer.Serialize(
                    new
                    {
                        transport = "app-server",
                        sessionMode = "SupervisorFork",
                        sessionId = session.Id,
                        parentProviderThreadId,
                        providerThreadId = childThreadId,
                        providerTurnId,
                        logicalTurnId,
                        model,
                        forkedModel,
                        reasoning = reasoningEffort,
                        inheritedEffort,
                        finalEffort,
                        tokenUsage = usage
                    },
                    JsonOptions);

            return new CodexStructuredRunResult(
                new ProcessRunResult(
                    "codex app-server",
                    [],
                    0,
                    false,
                    stopwatch.ElapsedMilliseconds,
                    string.Empty,
                    string.Empty),
                finalMessage,
                details,
                usage,
                session.Id,
                childThreadId,
                1,
                logicalTurnId);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(
                        entireProcessTree: true);
                }
                catch
                {
                }
            }

            try
            {
                await process.WaitForExitAsync(
                    CancellationToken.None);
            }
            catch
            {
            }

            _ = await stderrTask;
        }
    }

    private static string BuildAppServerCommand() =>
        "exec codex --enable reasoning_effort_override " +
        "--disable apps --disable plugins --disable multi_agent " +
        "--disable memories app-server --listen stdio://";

    private static TokenUsage ParseUsage(
        JsonElement last)
    {
        static long Number(
            JsonElement element,
            string property) =>
            element.TryGetProperty(
                property,
                out var value) &&
            value.ValueKind ==
                JsonValueKind.Number
                ? value.GetInt64()
                : 0;

        return new TokenUsage(
            Number(last, "inputTokens"),
            Number(last, "cachedInputTokens"),
            Number(last, "outputTokens"),
            Number(last, "reasoningOutputTokens"),
            Number(last, "totalTokens"))
        {
            CacheWriteInputTokens =
                Number(
                    last,
                    "cacheWriteInputTokens")
        };
    }

    private static string ExtractFinalAgentMessage(
        JsonElement turn)
    {
        if (!turn.TryGetProperty(
                "items",
                out var items) ||
            items.ValueKind !=
                JsonValueKind.Array)
        {
            return string.Empty;
        }

        string? final = null;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty(
                    "type",
                    out var type) &&
                string.Equals(
                    type.GetString(),
                    "agentMessage",
                    StringComparison.Ordinal) &&
                item.TryGetProperty(
                    "text",
                    out var text))
            {
                final = text.GetString();
            }
        }

        return final?.Trim() ??
            string.Empty;
    }

    private sealed class JsonRpcPipe(
        StreamWriter writer,
        StreamReader reader)
    {
        private long _nextId;
        private readonly Queue<JsonElement> _backlog = new();

        public async Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            var id =
                Interlocked.Increment(
                    ref _nextId);

            await WriteAsync(
                new
                {
                    id,
                    method,
                    @params = parameters
                },
                cancellationToken);

            while (true)
            {
                var message =
                    await ReadRawAsync(
                        cancellationToken);

                if (message.TryGetProperty(
                        "id",
                        out var messageId) &&
                    messageId.ValueKind ==
                        JsonValueKind.Number &&
                    messageId.GetInt64() == id &&
                    (message.TryGetProperty(
                         "result",
                         out _) ||
                     message.TryGetProperty(
                         "error",
                         out _)))
                {
                    if (message.TryGetProperty(
                            "error",
                            out var error))
                    {
                        throw new InvalidOperationException(
                            $"{method} failed: {error}");
                    }

                    return message.GetProperty(
                            "result")
                        .Clone();
                }

                if (IsServerRequest(message))
                {
                    await ReplyUnsupportedAsync(
                        message,
                        cancellationToken);
                    continue;
                }

                _backlog.Enqueue(
                    message.Clone());
            }
        }

        public Task NotifyAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken) =>
            WriteAsync(
                new
                {
                    method,
                    @params = parameters
                },
                cancellationToken);

        public async Task<JsonElement> ReadNextAsync(
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var message =
                    _backlog.Count > 0
                        ? _backlog.Dequeue()
                        : await ReadRawAsync(
                            cancellationToken);

                if (!IsServerRequest(
                        message))
                {
                    return message;
                }

                await ReplyUnsupportedAsync(
                    message,
                    cancellationToken);
            }
        }

        private async Task<JsonElement> ReadRawAsync(
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var line =
                    await reader.ReadLineAsync(
                        cancellationToken);

                if (line is null)
                {
                    throw new EndOfStreamException(
                        "Codex app-server stdout closed.");
                }

                if (string.IsNullOrWhiteSpace(
                        line))
                {
                    continue;
                }

                try
                {
                    using var document =
                        JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // App-server stdout is expected to be JSONL. Ignore
                    // incidental non-JSON diagnostics instead of creating a
                    // second stdout reader.
                }
            }
        }

        private async Task WriteAsync(
            object message,
            CancellationToken cancellationToken)
        {
            var json =
                JsonSerializer.Serialize(
                    message,
                    JsonOptions);

            await writer.WriteLineAsync(
                json.AsMemory(),
                cancellationToken);
            await writer.FlushAsync(
                cancellationToken);
        }

        private static bool IsServerRequest(
            JsonElement message) =>
            message.TryGetProperty(
                "id",
                out _) &&
            message.TryGetProperty(
                "method",
                out _);

        private Task ReplyUnsupportedAsync(
            JsonElement request,
            CancellationToken cancellationToken)
        {
            var id =
                request.GetProperty("id");

            return WriteAsync(
                new
                {
                    id,
                    error = new
                    {
                        code = -32601,
                        message =
                            "LoopGolem experimental fork transport does not implement server requests."
                    }
                },
                cancellationToken);
        }
    }
}
