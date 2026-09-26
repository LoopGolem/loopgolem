using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public interface ICodexWorkerAppServerTransport
{
    Task<CodexStructuredRunResult> RunFreshAsync(
        CodexStructuredRunRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexStructuredRunResult> RunForkAsync(
        CodexStructuredRunRequest request,
        string parentProviderThreadId,
        CancellationToken cancellationToken = default);

    Task<CodexAllowanceSnapshot> ReadAllowanceAsync(
        CancellationToken cancellationToken = default);
}

public sealed class CodexAppServerWorkerTransport :
    ICodexWorkerAppServerTransport,
    IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TurnTimeout =
        TimeSpan.FromMinutes(60);
    private static readonly TimeSpan UsageGrace =
        TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly CodexCliService _runtime;
    private readonly IMissionStore _store;
    private readonly WslRuntimeService _wsl;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>
        _pending = new();
    private readonly Channel<JsonElement> _notifications =
        Channel.CreateUnbounded<JsonElement>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutReader;
    private Task? _stderrReader;
    private readonly StringBuilder _stderr = new();
    private string? _distribution;
    private long _nextRequestId;
    private bool _initialized;

    public CodexAppServerWorkerTransport(
        ProcessRunner processRunner,
        CodexCliService runtime,
        IMissionStore store)
    {
        _runtime = runtime;
        _store = store;
        _wsl = new WslRuntimeService(processRunner);
    }

    public Task<CodexStructuredRunResult> RunFreshAsync(
        CodexStructuredRunRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            request,
            parentProviderThreadId: null,
            cancellationToken);

    public Task<CodexStructuredRunResult> RunForkAsync(
        CodexStructuredRunRequest request,
        string parentProviderThreadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            parentProviderThreadId);

        return RunAsync(
            request,
            parentProviderThreadId,
            cancellationToken);
    }

    public async Task<CodexAllowanceSnapshot> ReadAllowanceAsync(
        CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(
            cancellationToken);
        try
        {
            await EnsureStartedAsync(
                cancellationToken);

            var response =
                await SendRequestAsync(
                    "account/rateLimits/read",
                    parameters: null,
                    cancellationToken);

            return ParseAllowanceSnapshot(
                response);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<CodexStructuredRunResult> RunAsync(
        CodexStructuredRunRequest request,
        string? parentProviderThreadId,
        CancellationToken cancellationToken)
    {
        if (request.Role != AgentSessionRole.Worker)
        {
            throw new InvalidOperationException(
                "The app-server worker transport accepts Worker turns only.");
        }

        if (request.SessionMode != CodexSessionMode.FreshEphemeral)
        {
            throw new InvalidOperationException(
                "App-server benchmark workers are task-scoped and must use FreshEphemeral logical sessions.");
        }

        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);

            var workspace =
                await ConvertWorkspaceAsync(
                    request.Workspace,
                    cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var session = new AgentSession(
                Guid.NewGuid().ToString("N"),
                request.MissionId,
                AgentSessionRole.Worker,
                request.Model,
                request.ReasoningEffort,
                null,
                AgentSessionStatus.Active,
                request.LeaseOwnerTaskId,
                0,
                0,
                null,
                now,
                now,
                now);

            await _store.UpsertAgentSessionAsync(
                session,
                cancellationToken);

            const int turnNumber = 1;
            var turn = new AgentTurn(
                Guid.NewGuid().ToString("N"),
                request.MissionId,
                request.TaskId,
                session.Id,
                AgentTurnPurpose.Work,
                request.Model,
                request.ReasoningEffort,
                turnNumber,
                now,
                null,
                null,
                0,
                0,
                0,
                0,
                0,
                0);

            await _store.UpsertAgentTurnAsync(
                turn,
                cancellationToken);

            var providerThreadId =
                parentProviderThreadId is null
                    ? await StartFreshThreadAsync(
                        request,
                        workspace,
                        cancellationToken)
                    : await ForkThreadAsync(
                        request,
                        workspace,
                        parentProviderThreadId,
                        cancellationToken);

            session = session with
            {
                ProviderThreadId = providerThreadId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await _store.UpsertAgentSessionAsync(
                session,
                CancellationToken.None);

            var stopwatch = Stopwatch.StartNew();
            var outcome =
                await RunTurnAsync(
                    request,
                    providerThreadId,
                    cancellationToken);
            stopwatch.Stop();

            var completedAt = DateTimeOffset.UtcNow;
            var usage = outcome.Usage;

            turn = turn with
            {
                CompletedAtUtc = completedAt,
                DurationMilliseconds = outcome.DurationMilliseconds > 0
                    ? outcome.DurationMilliseconds
                    : stopwatch.ElapsedMilliseconds,
                InputTokens = usage?.InputTokens ?? 0,
                CachedInputTokens = usage?.CachedInputTokens ?? 0,
                CacheWriteInputTokens =
                    usage?.CacheWriteInputTokens ?? 0,
                OutputTokens = usage?.OutputTokens ?? 0,
                ReasoningOutputTokens =
                    usage?.ReasoningOutputTokens ?? 0,
                TotalTokens = usage?.TotalTokens ?? 0
            };

            await _store.UpsertAgentTurnAsync(
                turn,
                CancellationToken.None);

            session = session with
            {
                Status = outcome.Success
                    ? AgentSessionStatus.Closed
                    : AgentSessionStatus.Invalidated,
                TurnCount = turnNumber,
                MicrotaskCount = 0,
                LeaseOwnerTaskId = null,
                TerminationReason = outcome.Success
                    ? parentProviderThreadId is null
                        ? "appserver_fresh_ephemeral"
                        : "appserver_supervisor_fork_ephemeral"
                    : "appserver_turn_failed",
                LastUsedAtUtc = completedAt,
                UpdatedAtUtc = completedAt
            };

            await _store.UpsertAgentSessionAsync(
                session,
                CancellationToken.None);

            var details = JsonSerializer.Serialize(
                new
                {
                    transport = "app-server",
                    strategy = parentProviderThreadId is null
                        ? "fresh"
                        : "supervisor_fork",
                    sessionId = session.Id,
                    providerThreadId,
                    parentProviderThreadId,
                    turnNumber,
                    model = request.Model,
                    reasoning = request.ReasoningEffort,
                    finalReasoningEffort =
                        outcome.FinalReasoningEffort,
                    sandbox = request.Sandbox,
                    tokenUsage = usage,
                    durationMilliseconds =
                        turn.DurationMilliseconds
                },
                JsonOptions);

            var process = new ProcessRunResult(
                "codex app-server",
                [],
                outcome.Success ? 0 : 1,
                false,
                turn.DurationMilliseconds ?? stopwatch.ElapsedMilliseconds,
                string.Empty,
                outcome.Error ?? string.Empty);

            return new CodexStructuredRunResult(
                process,
                outcome.FinalMessage.Trim(),
                details,
                usage,
                session.Id,
                providerThreadId,
                turnNumber,
                turn.Id);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<string> StartFreshThreadAsync(
        CodexStructuredRunRequest request,
        string workspace,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            "thread/start",
            new
            {
                model = request.Model,
                cwd = workspace,
                approvalPolicy = "never",
                sandbox = ToSandboxMode(request.Sandbox),
                ephemeral = true,
                config = new Dictionary<string, object?>
                {
                    ["model_reasoning_effort"] =
                        request.ReasoningEffort
                }
            },
            cancellationToken);

        var threadId = GetThreadId(response);

        var model = GetString(response, "model");
        if (!string.Equals(
                model,
                request.Model,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"app-server started model '{model}' instead of '{request.Model}'.");
        }

        return threadId;
    }

    private async Task<string> ForkThreadAsync(
        CodexStructuredRunRequest request,
        string workspace,
        string parentProviderThreadId,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            "thread/fork",
            new
            {
                threadId = parentProviderThreadId,
                cwd = workspace,
                approvalPolicy = "never",
                sandbox = ToSandboxMode(request.Sandbox),
                ephemeral = true,
                excludeTurns = true
            },
            cancellationToken);

        var threadId = GetThreadId(response);
        var inheritedEffort =
            GetString(response, "reasoningEffort");

        if (!string.Equals(
                inheritedEffort,
                "high",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Supervisor fork must inherit HIGH before the Worker turn, but app-server reported '{inheritedEffort ?? "(null)"}'.");
        }

        return threadId;
    }

    private async Task<TurnOutcome> RunTurnAsync(
        CodexStructuredRunRequest request,
        string providerThreadId,
        CancellationToken cancellationToken)
    {
        using var schema =
            JsonDocument.Parse(request.Schema);

        var response = await SendRequestAsync(
            "turn/start",
            new
            {
                threadId = providerThreadId,
                input = new[]
                {
                    new
                    {
                        type = "text",
                        text = request.Prompt,
                        text_elements =
                            Array.Empty<object>()
                    }
                },
                effort = request.ReasoningEffort,
                outputSchema =
                    schema.RootElement.Clone()
            },
            cancellationToken);

        if (!response.TryGetProperty(
                "turn",
                out var turnElement) ||
            !turnElement.TryGetProperty(
                "id",
                out var turnIdElement) ||
            turnIdElement.ValueKind !=
                JsonValueKind.String)
        {
            throw new InvalidDataException(
                "turn/start returned no turn id.");
        }

        var turnId = turnIdElement.GetString()
            ?? throw new InvalidDataException(
                "turn/start returned an empty turn id.");

        TokenUsage? usage = null;
        string finalMessage = string.Empty;
        JsonElement? completedTurn = null;

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(TurnTimeout);

        while (completedTurn is null)
        {
            var message =
                await _notifications.Reader.ReadAsync(
                    timeout.Token);

            if (!TryGetMethod(
                    message,
                    out var method) ||
                !message.TryGetProperty(
                    "params",
                    out var parameters))
            {
                continue;
            }

            if (string.Equals(
                    method,
                    "thread/tokenUsage/updated",
                    StringComparison.Ordinal) &&
                IsTurn(parameters, turnId) &&
                TryParseUsage(
                    parameters,
                    out var observedUsage))
            {
                usage = observedUsage;
                continue;
            }

            if (string.Equals(
                    method,
                    "item/completed",
                    StringComparison.Ordinal) &&
                IsTurn(parameters, turnId) &&
                TryGetAgentMessage(
                    parameters,
                    out var text))
            {
                finalMessage = text;
                continue;
            }

            if (string.Equals(
                    method,
                    "turn/completed",
                    StringComparison.Ordinal) &&
                parameters.TryGetProperty(
                    "turn",
                    out var completed) &&
                string.Equals(
                    GetString(completed, "id"),
                    turnId,
                    StringComparison.Ordinal))
            {
                completedTurn = completed.Clone();

                var fromTurn =
                    GetLastAgentMessage(completed);
                if (!string.IsNullOrWhiteSpace(
                        fromTurn))
                {
                    finalMessage = fromTurn;
                }
            }
        }

        if (usage is null)
        {
            using var grace =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            grace.CancelAfter(UsageGrace);

            try
            {
                while (usage is null)
                {
                    var message =
                        await _notifications.Reader.ReadAsync(
                            grace.Token);

                    if (TryGetMethod(
                            message,
                            out var method) &&
                        string.Equals(
                            method,
                            "thread/tokenUsage/updated",
                            StringComparison.Ordinal) &&
                        message.TryGetProperty(
                            "params",
                            out var parameters) &&
                        IsTurn(parameters, turnId) &&
                        TryParseUsage(
                            parameters,
                            out var observedUsage))
                    {
                        usage = observedUsage;
                    }
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        var turn = completedTurn.Value;
        var status = GetString(
            turn,
            "status");
        var success = string.Equals(
            status,
            "completed",
            StringComparison.OrdinalIgnoreCase);
        var error = success
            ? null
            : turn.TryGetProperty(
                "error",
                out var errorElement)
                ? errorElement.GetRawText()
                : $"app-server turn ended with status '{status}'.";

        var duration =
            turn.TryGetProperty(
                "durationMs",
                out var durationElement) &&
            durationElement.TryGetInt64(
                out var durationMs)
                ? durationMs
                : 0;

        var read = await SendRequestAsync(
            "thread/read",
            new
            {
                threadId = providerThreadId,
                includeTurns = false
            },
            cancellationToken);

        var finalEffort =
            read.TryGetProperty(
                "thread",
                out var readThread)
                ? GetString(
                    readThread,
                    "reasoningEffort")
                : null;

        return new TurnOutcome(
            success,
            finalMessage,
            usage,
            duration,
            finalEffort,
            error);
    }

    private async Task EnsureStartedAsync(
        CancellationToken cancellationToken)
    {
        if (_initialized &&
            _process is { HasExited: false })
        {
            return;
        }

        if (_process is { HasExited: true })
        {
            throw new InvalidOperationException(
                "The Codex app-server process exited. Restart the LoopGolem Worker before retrying this experimental transport.");
        }

        var status =
            await _runtime.GetStatusAsync(
                cancellationToken);

        if (!status.Available ||
            !status.ChatGptAuthenticated)
        {
            throw new InvalidOperationException(
                status.Message);
        }

        var startInfo =
            CreateStartInfo(status);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Could not start Codex app-server.");
        }

        _process = process;
        _stdin = process.StandardInput;
        _distribution = status.Distribution;
        _stdoutReader = ReadStdoutAsync(process);
        _stderrReader = ReadStderrAsync(process);

        await SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "loopgolem",
                    title = "LoopGolem",
                    version = "0.1.0"
                },
                capabilities = new
                {
                    experimentalApi = false,
                    requestAttestation = false
                }
            },
            cancellationToken);

        await SendNotificationAsync(
            "initialized",
            cancellationToken);

        _initialized = true;
    }

    private static ProcessStartInfo CreateStartInfo(
        LoopGolem.Core.Protocol.CodexRuntimeStatus status)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        var appServerArguments = new[]
        {
            "--enable", "reasoning_effort_override",
            "--disable", "apps",
            "--disable", "plugins",
            "--disable", "multi_agent",
            "--disable", "memories",
            "--config", "approval_policy=never",
            "app-server",
            "--listen", "stdio://"
        };

        if (OperatingSystem.IsWindows())
        {
            var distribution =
                status.Distribution
                ?? throw new InvalidOperationException(
                    "WSL distribution is unavailable.");

            startInfo.FileName = "wsl.exe";
            startInfo.ArgumentList.Add(
                "--distribution");
            startInfo.ArgumentList.Add(
                distribution);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(
                "bash");
            startInfo.ArgumentList.Add(
                "-lc");

            var command =
                "exec codex " +
                string.Join(
                    " ",
                    appServerArguments.Select(
                        BashQuote));

            startInfo.ArgumentList.Add(
                command);
            startInfo.WorkingDirectory =
                Environment.CurrentDirectory;
        }
        else
        {
            startInfo.FileName = "codex";
            foreach (var argument in
                     appServerArguments)
            {
                startInfo.ArgumentList.Add(
                    argument);
            }

            startInfo.WorkingDirectory =
                Environment.CurrentDirectory;
        }

        return startInfo;
    }

    private async Task<string> ConvertWorkspaceAsync(
        string workspace,
        CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? await _wsl.ConvertWindowsPathAsync(
                _distribution
                    ?? throw new InvalidOperationException(
                        "WSL distribution is unavailable."),
                workspace,
                cancellationToken)
            : workspace;

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        if (_process is null ||
            _stdin is null)
        {
            throw new InvalidOperationException(
                "Codex app-server has not started.");
        }

        var id =
            Interlocked.Increment(
                ref _nextRequestId);
        var completion =
            new TaskCompletionSource<JsonElement>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        if (!_pending.TryAdd(
                id,
                completion))
        {
            throw new InvalidOperationException(
                "Duplicate JSON-RPC request id.");
        }

        try
        {
            await WriteMessageAsync(
                new
                {
                    id,
                    method,
                    @params = parameters
                },
                cancellationToken);

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeout.CancelAfter(
                RequestTimeout);

            return await completion.Task.WaitAsync(
                timeout.Token);
        }
        finally
        {
            _pending.TryRemove(
                id,
                out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(
            new { method },
            cancellationToken);

    private async Task WriteMessageAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        var stdin = _stdin
            ?? throw new InvalidOperationException(
                "Codex app-server stdin is unavailable.");

        var json = JsonSerializer.Serialize(
            payload,
            JsonOptions);

        await _writeGate.WaitAsync(
            cancellationToken);
        try
        {
            await stdin.WriteLineAsync(
                json.AsMemory(),
                cancellationToken);
            await stdin.FlushAsync(
                cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(
        Process process)
    {
        try
        {
            while (await process.StandardOutput
                       .ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(
                        line))
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    using var document =
                        JsonDocument.Parse(
                            line);
                    message =
                        document.RootElement
                            .Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message.TryGetProperty(
                        "id",
                        out var idElement) &&
                    idElement.TryGetInt64(
                        out var id))
                {
                    if (message.TryGetProperty(
                            "method",
                            out _))
                    {
                        await ReplyUnsupportedAsync(
                            id,
                            CancellationToken.None);
                        continue;
                    }

                    if (_pending.TryGetValue(
                            id,
                            out var completion))
                    {
                        if (message.TryGetProperty(
                                "error",
                                out var error))
                        {
                            completion.TrySetException(
                                new InvalidOperationException(
                                    error.GetRawText()));
                        }
                        else if (message.TryGetProperty(
                                     "result",
                                     out var result))
                        {
                            completion.TrySetResult(
                                result.Clone());
                        }
                    }

                    continue;
                }

                await _notifications.Writer.WriteAsync(
                    message);
            }
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync();
                }
                catch
                {
                }
            }

            FailPending(
                new EndOfStreamException(
                    "Codex app-server stdout closed."));
        }
    }

    private async Task ReadStderrAsync(
        Process process)
    {
        while (await process.StandardError
                   .ReadLineAsync() is { } line)
        {
            lock (_stderr)
            {
                _stderr.AppendLine(line);
            }
        }
    }

    private Task ReplyUnsupportedAsync(
        long id,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(
            new
            {
                id,
                error = new
                {
                    code = -32601,
                    message =
                        "LoopGolem does not support this app-server request."
                }
            },
            cancellationToken);

    private void FailPending(
        Exception exception)
    {
        foreach (var completion in
                 _pending.Values)
        {
            completion.TrySetException(
                exception);
        }
    }

    private static CodexAllowanceSnapshot
        ParseAllowanceSnapshot(
            JsonElement response)
    {
        if (!response.TryGetProperty(
                "rateLimits",
                out var rateLimits))
        {
            throw new InvalidDataException(
                "account/rateLimits/read returned no rateLimits snapshot.");
        }

        static double? UsedPercent(
            JsonElement parent,
            string name) =>
            parent.TryGetProperty(
                    name,
                    out var window) &&
                window.ValueKind ==
                    JsonValueKind.Object &&
                window.TryGetProperty(
                    "usedPercent",
                    out var value) &&
                value.TryGetDouble(
                    out var number)
                ? number
                : null;

        static int? WindowMinutes(
            JsonElement parent,
            string name)
        {
            if (!parent.TryGetProperty(
                    name,
                    out var window) ||
                window.ValueKind !=
                    JsonValueKind.Object ||
                !window.TryGetProperty(
                    "windowDurationMins",
                    out var value) ||
                value.ValueKind ==
                    JsonValueKind.Null)
            {
                return null;
            }

            return value.TryGetInt32(
                out var number)
                ? number
                : null;
        }

        static long? ResetsAt(
            JsonElement parent,
            string name)
        {
            if (!parent.TryGetProperty(
                    name,
                    out var window) ||
                window.ValueKind !=
                    JsonValueKind.Object ||
                !window.TryGetProperty(
                    "resetsAt",
                    out var value) ||
                value.ValueKind ==
                    JsonValueKind.Null)
            {
                return null;
            }

            return value.TryGetInt64(
                out var number)
                ? number
                : null;
        }

        bool? ordinaryUsageAllowed = null;
        if (response.TryGetProperty(
                "ordinaryUsageAllowed",
                out var ordinary) &&
            ordinary.ValueKind is
                JsonValueKind.True or
                JsonValueKind.False)
        {
            ordinaryUsageAllowed =
                ordinary.GetBoolean();
        }

        return new CodexAllowanceSnapshot(
            DateTimeOffset.UtcNow,
            UsedPercent(
                rateLimits,
                "primary"),
            WindowMinutes(
                rateLimits,
                "primary"),
            ResetsAt(
                rateLimits,
                "primary"),
            UsedPercent(
                rateLimits,
                "secondary"),
            WindowMinutes(
                rateLimits,
                "secondary"),
            ResetsAt(
                rateLimits,
                "secondary"),
            ordinaryUsageAllowed,
            GetString(
                rateLimits,
                "limitId"),
            GetString(
                rateLimits,
                "limitName"));
    }

    private static string GetThreadId(
        JsonElement response)
    {
        if (!response.TryGetProperty(
                "thread",
                out var thread) ||
            !thread.TryGetProperty(
                "id",
                out var id) ||
            id.ValueKind !=
                JsonValueKind.String ||
            string.IsNullOrWhiteSpace(
                id.GetString()))
        {
            throw new InvalidDataException(
                "app-server returned no thread id.");
        }

        return id.GetString()!;
    }

    private static string ToSandboxMode(
        string sandbox) =>
        string.Equals(
            sandbox,
            "read-only",
            StringComparison.Ordinal)
            ? "read-only"
            : "workspace-write";

    private static bool TryGetMethod(
        JsonElement message,
        out string? method)
    {
        method = null;

        if (!message.TryGetProperty(
                "method",
                out var methodElement) ||
            methodElement.ValueKind !=
                JsonValueKind.String)
        {
            return false;
        }

        method = methodElement.GetString();
        return !string.IsNullOrWhiteSpace(
            method);
    }

    private static bool IsTurn(
        JsonElement parameters,
        string turnId) =>
        string.Equals(
            GetString(
                parameters,
                "turnId"),
            turnId,
            StringComparison.Ordinal);

    private static bool TryGetAgentMessage(
        JsonElement parameters,
        out string text)
    {
        text = string.Empty;

        if (!parameters.TryGetProperty(
                "item",
                out var item) ||
            !string.Equals(
                GetString(
                    item,
                    "type"),
                "agentMessage",
                StringComparison.Ordinal) ||
            !item.TryGetProperty(
                "text",
                out var textElement) ||
            textElement.ValueKind !=
                JsonValueKind.String)
        {
            return false;
        }

        text =
            textElement.GetString()
            ?? string.Empty;
        return true;
    }

    private static string GetLastAgentMessage(
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

        string result = string.Empty;

        foreach (var item in
                 items.EnumerateArray())
        {
            if (string.Equals(
                    GetString(
                        item,
                        "type"),
                    "agentMessage",
                    StringComparison.Ordinal) &&
                item.TryGetProperty(
                    "text",
                    out var text) &&
                text.ValueKind ==
                    JsonValueKind.String)
            {
                result =
                    text.GetString()
                    ?? result;
            }
        }

        return result;
    }

    private static bool TryParseUsage(
        JsonElement parameters,
        out TokenUsage usage)
    {
        usage = new TokenUsage(
            0,
            0,
            0,
            0,
            0);

        if (!parameters.TryGetProperty(
                "tokenUsage",
                out var tokenUsage) ||
            !tokenUsage.TryGetProperty(
                "last",
                out var last))
        {
            return false;
        }

        static long Number(
            JsonElement element,
            string name) =>
            element.TryGetProperty(
                    name,
                    out var value) &&
                value.TryGetInt64(
                    out var number)
                ? number
                : 0;

        usage = new TokenUsage(
            Number(
                last,
                "inputTokens"),
            Number(
                last,
                "cachedInputTokens"),
            Number(
                last,
                "outputTokens"),
            Number(
                last,
                "reasoningOutputTokens"),
            Number(
                last,
                "totalTokens"))
        {
            CacheWriteInputTokens =
                Number(
                    last,
                    "cacheWriteInputTokens")
        };

        return true;
    }

    private static string? GetString(
        JsonElement element,
        string name) =>
        element.TryGetProperty(
                name,
                out var value) &&
            value.ValueKind ==
                JsonValueKind.String
            ? value.GetString()
            : null;

    private static string BashQuote(
        string value) =>
        value.Length == 0
            ? "''"
            : "'" +
              value.Replace(
                  "'",
                  "'\"'\"'",
                  StringComparison.Ordinal) +
              "'";

    public async ValueTask DisposeAsync()
    {
        _initialized = false;

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(
                    entireProcessTree: true);
            }
        }
        catch
        {
        }

        if (_stdoutReader is not null)
        {
            try
            {
                await _stdoutReader;
            }
            catch
            {
            }
        }

        if (_stderrReader is not null)
        {
            try
            {
                await _stderrReader;
            }
            catch
            {
            }
        }

        _stdin?.Dispose();
        _process.Dispose();
        _executionGate.Dispose();
        _writeGate.Dispose();
    }

    private sealed record TurnOutcome(
        bool Success,
        string FinalMessage,
        TokenUsage? Usage,
        long DurationMilliseconds,
        string? FinalReasoningEffort,
        string? Error);
}
