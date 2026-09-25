using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed class CodexPlanningService(
    ProcessRunner processRunner,
    CodexCliService runtime)
{
    private sealed record StructuredRunResult(
        ProcessRunResult Process,
        string FinalMessage,
        string Details,
        TokenUsage? TokenUsage);

    public const string PlannerModel = "gpt-6-luna";
    public const string PlannerReasoning = "high";
    public const string WorkerModel = "gpt-6-luna";
    public const string WorkerReasoning = "low";

    internal const string SelfHostingRule =
        "SELF-HOSTING RULE: if this mission modifies LoopGolem.Core, " +
        "LoopGolem.Orchestrator, or LoopGolem.Worker, the currently running " +
        "Worker will NOT hot-reload those changes. Do not make later tasks " +
        "depend on newly implemented Worker runtime behavior becoming active " +
        "in this same mission. Source/build/test checks may launch newly built " +
        "child processes, but the current orchestrator process remains on its " +
        "original binary.";

    private static readonly TimeSpan ExecutionTimeout =
        TimeSpan.FromMinutes(60);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WslRuntimeService _wsl = new(processRunner);
    private readonly GitSnapshotService _snapshots = new(processRunner);

    public async Task<TaskExecutionResult> PlanAsync(
        Mission mission,
        CancellationToken cancellationToken = default)
    {
        var status = await runtime.GetStatusAsync(cancellationToken);
        var runtimeError = ValidateRuntime(status);
        if (runtimeError is not null)
        {
            return runtimeError;
        }

        var cleanError = await EnsureCleanGitAsync(
            mission.WorkspacePath,
            cancellationToken);
        if (cleanError is not null)
        {
            return cleanError;
        }

        var baseCommit = await _snapshots.GetHeadCommitAsync(
            mission.WorkspacePath,
            cancellationToken);

        var run = await RunStructuredAsync(
            mission.WorkspacePath,
            PlannerModel,
            PlannerReasoning,
            "read-only",
            PlannerSchema,
            BuildPlannerPrompt(mission),
            cancellationToken);

        DevelopmentDiagnostics.Write(
            "planner.raw",
            mission.Id,
            run.FinalMessage);

        if (run.Process.TimedOut)
        {
            return TaskExecutionResult.Failed(
                "Planner timed out.",
                "GPT-6 Luna High exceeded the planner timeout.",
                run.Details, run.TokenUsage);
        }

        if (run.Process.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                $"Planner exited with code {run.Process.ExitCode}.",
                GetProcessError(run.Process),
                run.Details, run.TokenUsage);
        }

        MissionPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<MissionPlan>(
                run.FinalMessage,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Planner returned invalid JSON.",
                exception.Message,
                run.Details, run.TokenUsage);
        }

        if (plan is null)
        {
            return TaskExecutionResult.Failed(
                "Planner returned no mission plan.",
                "The structured planner response was empty.",
                run.Details, run.TokenUsage);
        }

        var validationError = MissionPlanValidator.Validate(plan);
        if (validationError is not null)
        {
            return TaskExecutionResult.Failed(
                "Planner returned an invalid mission plan.",
                validationError,
                run.Details, run.TokenUsage);
        }

        return TaskExecutionResult.Succeeded(
            plan.Summary,
            JsonSerializer.Serialize(
                new PlannerResult(baseCommit, plan),
                JsonOptions));
    }

    public async Task<TaskExecutionResult> ValidateAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        if (task.Definition is null ||
            task.Kind != MissionTaskKind.ValidateMission)
        {
            return TaskExecutionResult.Failed(
                "Validator task definition is missing.",
                "LoopGolem did not provide validator context.");
        }

        ValidatorContext? context;
        try
        {
            context = JsonSerializer.Deserialize<ValidatorContext>(
                task.Definition.Prompt,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Validator context is invalid.",
                exception.Message);
        }

        if (context is null ||
            string.IsNullOrWhiteSpace(context.BaseCommit))
        {
            return TaskExecutionResult.Failed(
                "Validator context is incomplete.",
                "The base commit or original plan is missing.");
        }

        var status = await runtime.GetStatusAsync(cancellationToken);
        var runtimeError = ValidateRuntime(status);
        if (runtimeError is not null)
        {
            return runtimeError;
        }

        string snapshotCommit;
        try
        {
            snapshotCommit = await _snapshots.CreateSnapshotCommitAsync(
                mission.WorkspacePath,
                context.BaseCommit,
                mission.Id,
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return TaskExecutionResult.Failed(
                "Could not create the validation snapshot.",
                exception.Message);
        }

        var run = await RunStructuredAsync(
            mission.WorkspacePath,
            PlannerModel,
            PlannerReasoning,
            "read-only",
            ValidatorSchema,
            BuildValidatorPrompt(
                mission,
                context,
                snapshotCommit),
            cancellationToken);

        DevelopmentDiagnostics.Write(
            $"validator.raw:{context.Cycle}",
            mission.Id,
            run.FinalMessage);

        if (run.Process.TimedOut)
        {
            return TaskExecutionResult.Failed(
                "Validator timed out.",
                "GPT-6 Luna High exceeded the validator timeout.",
                run.Details, run.TokenUsage);
        }

        if (run.Process.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                $"Validator exited with code {run.Process.ExitCode}.",
                GetProcessError(run.Process),
                run.Details, run.TokenUsage);
        }

        ValidationResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ValidationResult>(
                run.FinalMessage,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Validator returned invalid JSON.",
                exception.Message,
                run.Details, run.TokenUsage);
        }

        if (result is null ||
            result.Status is not ("ok" or "not_ok"))
        {
            return TaskExecutionResult.Failed(
                "Validator returned an unsupported result.",
                result?.Status ?? "The structured result was empty.",
                run.Details, run.TokenUsage);
        }

        if (result.Status == "ok" && result.Tasks.Count != 0)
        {
            return TaskExecutionResult.Failed(
                "Validator returned correction tasks with status ok.",
                "An ok validation must return an empty task list.",
                run.Details, run.TokenUsage);
        }

        if (result.Status == "not_ok")
        {
            var taskError = MissionPlanValidator.ValidateTasks(result.Tasks);
            if (taskError is not null)
            {
                return TaskExecutionResult.Failed(
                    "Validator returned an invalid correction plan.",
                    taskError,
                    run.Details, run.TokenUsage);
            }

            if (result.Tasks.Count == 0)
            {
                return TaskExecutionResult.Failed(
                    "Validator returned not_ok without correction tasks.",
                    "At least one correction task is required.",
                    run.Details, run.TokenUsage);
            }

            var expectedPrefix = $"fix{context.Cycle}_";
            if (result.Tasks.Any(
                    correction =>
                        !correction.Id.StartsWith(
                            expectedPrefix,
                            StringComparison.Ordinal)))
            {
                return TaskExecutionResult.Failed(
                    "Validator returned correction ids outside the required namespace.",
                    $"Every correction id in cycle {context.Cycle} must start with '{expectedPrefix}'.",
                    run.Details, run.TokenUsage);
            }
        }

        return TaskExecutionResult.Succeeded(
            result.Summary,
            JsonSerializer.Serialize(
                new ValidatorExecutionResult(
                    snapshotCommit,
                    result),
                JsonOptions));
    }

    public async Task<TaskExecutionResult> ExecuteMicroTaskAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var definition = task.Definition;
        if (definition is null ||
            definition.Executor != PlannedExecutorKinds.LunaLow)
        {
            return TaskExecutionResult.Failed(
                "Luna Low task definition is missing.",
                "The planner did not provide a valid Luna Low microtask.");
        }

        var status = await runtime.GetStatusAsync(cancellationToken);
        var runtimeError = ValidateRuntime(status);
        if (runtimeError is not null)
        {
            return runtimeError;
        }

        if (string.IsNullOrWhiteSpace(task.ExecutionContext))
        {
            return TaskExecutionResult.Failed(
                "Luna Low execution baseline is missing.",
                "The task was not prepared for crash-safe execution.");
        }

        GitWorkspaceSnapshot? before;
        try
        {
            before = JsonSerializer.Deserialize<GitWorkspaceSnapshot>(
                task.ExecutionContext,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Luna Low execution baseline is invalid.",
                exception.Message);
        }

        if (before is null)
        {
            return TaskExecutionResult.Failed(
                "Luna Low execution baseline is empty.",
                "The persisted workspace snapshot could not be restored.");
        }

        var run = await RunStructuredAsync(
            mission.WorkspacePath,
            WorkerModel,
            WorkerReasoning,
            "workspace-write",
            WorkerSchema,
            BuildWorkerPrompt(definition),
            cancellationToken);

        DevelopmentDiagnostics.Write(
            $"worker.raw:{definition.Id}",
            mission.Id,
            run.FinalMessage);

        if (run.Process.TimedOut)
        {
            return TaskExecutionResult.Failed(
                "Luna Low microtask timed out.",
                "The microtask exceeded the one-hour timeout.",
                run.Details, run.TokenUsage);
        }

        if (run.Process.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                $"Luna Low exited with code {run.Process.ExitCode}.",
                GetProcessError(run.Process),
                run.Details, run.TokenUsage);
        }

        CodexAgentOutcome? outcome;
        try
        {
            outcome = JsonSerializer.Deserialize<CodexAgentOutcome>(
                run.FinalMessage,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Luna Low returned invalid structured output.",
                exception.Message,
                run.Details, run.TokenUsage);
        }

        if (outcome is null)
        {
            return TaskExecutionResult.Failed(
                "Luna Low returned no structured output.",
                "The final response was empty.",
                run.Details, run.TokenUsage);
        }

        GitWorkspaceSnapshot after;
        try
        {
            after = await GitWorkspaceSnapshot.CaptureAsync(
                processRunner,
                mission.WorkspacePath,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return TaskExecutionResult.Failed(
                "Could not verify the workspace after the microtask.",
                exception.Message,
                run.Details, run.TokenUsage);
        }

        var changedByTask = after.ChangesSince(before);
        var allowed = definition.WriteFiles
            .Select(GitWorkspaceSnapshot.Normalize)
            .ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

        var violations = changedByTask
            .Where(path => !allowed.Contains(path))
            .ToArray();

        if (violations.Length > 0)
        {
            return TaskExecutionResult.Failed(
                "Luna Low modified files outside its write allowlist.",
                string.Join(", ", violations),
                run.Details, run.TokenUsage);
        }

        return outcome.Outcome switch
        {
            "blocked" => TaskExecutionResult.Failed(
                outcome.Summary,
                string.IsNullOrWhiteSpace(outcome.Blocker)
                    ? "Luna Low reported a blocker."
                    : outcome.Blocker,
                run.Details, run.TokenUsage),
            "changed" when changedByTask.Count == 0 =>
                TaskExecutionResult.Failed(
                    "Luna Low reported changes, but no file changed.",
                    "The structured result disagrees with deterministic Git verification.",
                    run.Details, run.TokenUsage),
            "changed" => TaskExecutionResult.Succeeded(
                outcome.Summary,
                run.Details, run.TokenUsage),
            "already_satisfied"
                when changedByTask.Count > 0 &&
                     task.Status != LoopGolem.Core.Domain.TaskStatus.Retrying =>
                TaskExecutionResult.Failed(
                    "Luna Low reported no edit was needed, but files changed.",
                    string.Join(", ", changedByTask),
                    run.Details, run.TokenUsage),
            "already_satisfied" => TaskExecutionResult.Succeeded(
                outcome.Summary,
                run.Details, run.TokenUsage),
            _ => TaskExecutionResult.Failed(
                "Luna Low returned an unsupported outcome.",
                outcome.Outcome,
                run.Details,
                run.TokenUsage)
        };
    }

    private async Task<StructuredRunResult>
        RunStructuredAsync(
            string workspace,
            string model,
            string reasoning,
            string sandbox,
            string schema,
            string prompt,
            CancellationToken cancellationToken)
    {
        var runtimeDirectory = Path.Combine(
            Path.GetTempPath(),
            "LoopGolem",
            "structured",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDirectory);

        var outputPath = Path.Combine(runtimeDirectory, "last-message.json");
        var schemaPath = Path.Combine(runtimeDirectory, "schema.json");

        await File.WriteAllTextAsync(schemaPath, schema, cancellationToken);

        try
        {
            ProcessRunResult process;

            if (OperatingSystem.IsWindows())
            {
                var status = await runtime.GetStatusAsync(cancellationToken);
                var distribution = status.Distribution
                    ?? throw new InvalidOperationException(
                        "WSL distribution is unavailable.");

                var wslWorkspace = await _wsl.ConvertWindowsPathAsync(
                    distribution,
                    workspace,
                    cancellationToken);
                var wslOutput = await _wsl.ConvertWindowsPathAsync(
                    distribution,
                    outputPath,
                    cancellationToken);
                var wslSchema = await _wsl.ConvertWindowsPathAsync(
                    distribution,
                    schemaPath,
                    cancellationToken);

                process = await _wsl.RunLoginShellExecutableAsync(
                    distribution,
                    "codex",
                    BuildArguments(
                        wslWorkspace,
                        model,
                        reasoning,
                        sandbox,
                        wslSchema,
                        wslOutput),
                    ExecutionTimeout,
                    cancellationToken,
                    prompt);
            }
            else
            {
                process = await processRunner.RunAsync(
                    "codex",
                    BuildArguments(
                        workspace,
                        model,
                        reasoning,
                        sandbox,
                        schemaPath,
                        outputPath),
                    workspace,
                    ExecutionTimeout,
                    cancellationToken,
                    prompt);
            }

            var finalMessage = File.Exists(outputPath)
                ? await File.ReadAllTextAsync(outputPath, cancellationToken)
                : string.Empty;

            var tokenUsage = ParseTokenUsage(
                process.StandardOutput);

            var details = JsonSerializer.Serialize(new
            {
                model,
                reasoning,
                sandbox,
                tokenUsage,
                process
            });

            return new StructuredRunResult(
                process,
                finalMessage.Trim(),
                details,
                tokenUsage);
        }
        finally
        {
            try
            {
                Directory.Delete(runtimeDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        string workspace,
        string model,
        string reasoning,
        string sandbox,
        string schemaPath,
        string outputPath) =>
        [
            "exec",
            "--json",
            "--ephemeral",
            "--ignore-user-config",
            "--disable", "apps",
            "--disable", "plugins",
            "--disable", "multi_agent",
            "--color", "never",
            "--sandbox", sandbox,
            "--cd", workspace,
            "--model", model,
            "--config", $"model_reasoning_effort=\"{reasoning}\"",
            "--config", "approval_policy=never",
            "--config", "sandbox_workspace_write.network_access=false",
            "--output-schema", schemaPath,
            "--output-last-message", outputPath,
            "-"
        ];

    internal static TokenUsage? ParseTokenUsage(
        string jsonLines)
    {
        TokenUsage? latest = null;

        foreach (var line in jsonLines.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (TryGetUsageElement(
                        root,
                        out var usage))
                {
                    latest = ParseUsageElement(usage);
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON diagnostics. The structured Codex stream
                // may coexist with launcher output on some runtimes.
            }
        }

        return latest;
    }

    private static bool TryGetUsageElement(
        JsonElement root,
        out JsonElement usage)
    {
        if (root.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(
                type.GetString(),
                "turn.completed",
                StringComparison.Ordinal) &&
            root.TryGetProperty("usage", out usage) &&
            usage.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.TryGetProperty("msg", out var message) &&
            TryGetLegacyTokenUsage(
                message,
                out usage))
        {
            return true;
        }

        if (root.TryGetProperty("payload", out var payload) &&
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
        if (container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(
                type.GetString(),
                "token_count",
                StringComparison.Ordinal) &&
            container.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty(
                    "total_token_usage",
                    out usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            if (info.TryGetProperty(
                    "last_token_usage",
                    out usage) &&
                usage.ValueKind == JsonValueKind.Object)
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
        var input = GetTokenCount(
            usage,
            "input_tokens");
        var cachedInput = GetTokenCount(
            usage,
            "cached_input_tokens");
        var cacheWriteInput = GetTokenCount(
            usage,
            "cache_write_input_tokens");
        var output = GetTokenCount(
            usage,
            "output_tokens");
        var reasoningOutput = GetTokenCount(
            usage,
            "reasoning_output_tokens");
        var total = GetTokenCount(
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
            CacheWriteInputTokens = cacheWriteInput
        };
    }

    private static long GetTokenCount(
        JsonElement usage,
        string propertyName) =>
        usage.TryGetProperty(
            propertyName,
            out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var count)
            ? count
            : 0;

    internal static string BuildPlannerPrompt(Mission mission) =>
        $"""
        You are the LoopGolem mission planner running as GPT-6 Luna High.
        You may inspect the entire repository, but you must not modify it.

        USER GOAL:
        {mission.Goal}

        Produce only the structured execution plan required by the schema.

        RULES:
        - Prefer many small, independently verifiable microtasks over broad tasks.
        - Every task id must be short, unique, stable, and referenced by dependsOn.
        - Use executor "deterministic" whenever the operation is exact and mechanical.
        - Deterministic operations are write_file, create_directory, rename_path, or run_command.
        - run_command is direct process execution: executable plus arguments, never a shell command string.
        - Use executor "luna_low" when implementation judgment is required.
        - Luna Low receives only its microtask prompt, readFiles, writeFiles, and acceptanceChecks.
        - Make readFiles and writeFiles precise repository-relative paths.
        - Luna Low may write ONLY writeFiles; include every file it must modify.
        - Use dependsOn whenever a task requires files or state produced by another task.
        - Never request a model above GPT-6 Luna. Human attention is preferred.
        - Keep each Luna Low prompt self-contained and small.
        - For Luna Low set deterministic.kind to "none" and leave unused deterministic strings empty.
        - For deterministic tasks the deterministic object must fully specify the one operation.
        - finalChecks lists repository-level checks that the final GPT-6 Luna High validator must review.
        - {SelfHostingRule}
        """;

    internal static string BuildValidatorPrompt(
        Mission mission,
        ValidatorContext context,
        string snapshotCommit)
    {
        var planJson = JsonSerializer.Serialize(
            context.Plan,
            JsonOptions);

        return $"""
        You are the LoopGolem final validator running as GPT-6 Luna High.
        You may inspect the entire repository, but you must not modify it.

        ORIGINAL USER GOAL:
        {mission.Goal}

        ORIGINAL PLAN:
        {planJson}

        PRIOR CORRECTION TASKS:
        {JsonSerializer.Serialize(context.CorrectionHistory, JsonOptions)}

        BASE COMMIT:
        {context.BaseCommit}

        SNAPSHOT COMMIT:
        {snapshotCommit}

        VALIDATION CYCLE:
        {context.Cycle}

        VALIDATION RULES:
        - Review the actual implementation, not worker claims.
        - Start with: git diff --stat {context.BaseCommit}..{snapshotCommit}
        - Inspect the full diff with: git diff {context.BaseCommit}..{snapshotCommit}
        - Read any repository files needed to judge correctness.
        - Check the original user goal, every planned task, acceptance criteria, and finalChecks.
        - The snapshot commit is an unreachable LoopGolem-created Git object. It does not move or modify the user's branch.
        - Return status "ok" only when the snapshot satisfies the original goal and no correction is necessary.
        - Otherwise return status "not_ok" with the smallest set of correction microtasks needed.
        - Prefer small independent correction tasks.
        - Use deterministic operations for exact mechanical corrections and luna_low for code or prose requiring judgment.
        - Correction task readFiles/writeFiles must be precise repository-relative paths.
        - Correction dependencies may refer only to other correction tasks in this response.
        - Use unique correction ids prefixed with "fix{context.Cycle}_".
        - {SelfHostingRule}
        - Never request or assume a model above GPT-6 Luna. If the work is too complex to validate safely, use not_ok with bounded corrective tasks; LoopGolem will stop for human attention after its cycle limit.
        - For luna_low set deterministic.kind to "none".
        - Do not commit, push, create branches, or modify files.
        """;
    }

    private static string BuildWorkerPrompt(PlannedTask task)
    {
        static string Lines(IEnumerable<string> values) =>
            string.Join(Environment.NewLine, values.Select(value => $"- {value}"));

        return $"""
        TASK {task.Id}: {task.Title}

        GOAL:
        {task.Prompt}

        READ FILES:
        {Lines(task.ReadFiles)}

        ALLOWED WRITE FILES:
        {Lines(task.WriteFiles)}

        ACCEPTANCE CHECKS:
        {Lines(task.AcceptanceChecks)}

        RULES:
        - This is one microtask. Do not broaden scope.
        - Read the listed files first.
        - Do not modify any file outside ALLOWED WRITE FILES.
        - Do not commit, push, create branches, or rewrite Git history.
        - Network access is disabled.
        - Run acceptance checks when practical.
        - Return "blocked" if blocked.
        - Return "changed" only if a permitted file actually changed.
        - Return "already_satisfied" only if no edit was required.
        """;
    }

    private async Task<TaskExecutionResult?> EnsureCleanGitAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var status = await processRunner.RunAsync(
            "git",
            ["status", "--porcelain=v1", "--untracked-files=all"],
            workspace,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (status.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                "Planner requires a Git repository.",
                GetProcessError(status));
        }

        return string.IsNullOrWhiteSpace(status.StandardOutput)
            ? null
            : TaskExecutionResult.Failed(
                "Planner requires a clean working tree.",
                "Commit, stash, or discard local changes before starting the autonomous mission.");
    }

    private static TaskExecutionResult? ValidateRuntime(
        CodexRuntimeStatus status)
    {
        if (!status.Available)
        {
            return TaskExecutionResult.Failed(
                "Codex runtime is unavailable.",
                status.Message);
        }

        if (!status.ChatGptAuthenticated)
        {
            return TaskExecutionResult.Failed(
                "Codex is not authenticated with ChatGPT.",
                status.Message);
        }

        return null;
    }

    private static string GetProcessError(ProcessRunResult process) =>
        string.IsNullOrWhiteSpace(process.StandardError)
            ? process.StandardOutput.Trim()
            : process.StandardError.Trim();

    private const string WorkerSchema =
        """
        {
          "type": "object",
          "properties": {
            "outcome": {
              "type": "string",
              "enum": ["changed", "already_satisfied", "blocked"]
            },
            "summary": { "type": "string" },
            "checks": {
              "type": "array",
              "items": { "type": "string" }
            },
            "blocker": { "type": "string" }
          },
          "required": ["outcome", "summary", "checks", "blocker"],
          "additionalProperties": false
        }
        """;

    private const string ValidatorSchema =
        """
        {
          "type": "object",
          "properties": {
            "status": {
              "type": "string",
              "enum": ["ok", "not_ok"]
            },
            "summary": { "type": "string" },
            "tasks": {
              "type": "array",
              "maxItems": 100,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "title": { "type": "string" },
                  "executor": {
                    "type": "string",
                    "enum": ["deterministic", "luna_low"]
                  },
                  "prompt": { "type": "string" },
                  "readFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "writeFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "acceptanceChecks": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "dependsOn": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "deterministic": {
                    "type": "object",
                    "properties": {
                      "kind": {
                        "type": "string",
                        "enum": ["none", "write_file", "create_directory", "rename_path", "run_command"]
                      },
                      "path": { "type": "string" },
                      "content": { "type": "string" },
                      "sourcePath": { "type": "string" },
                      "destinationPath": { "type": "string" },
                      "executable": { "type": "string" },
                      "arguments": {
                        "type": "array",
                        "items": { "type": "string" }
                      },
                      "workingDirectory": { "type": "string" },
                      "timeoutSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 900
                      }
                    },
                    "required": [
                      "kind", "path", "content", "sourcePath",
                      "destinationPath", "executable", "arguments",
                      "workingDirectory", "timeoutSeconds"
                    ],
                    "additionalProperties": false
                  }
                },
                "required": [
                  "id", "title", "executor", "prompt", "readFiles",
                  "writeFiles", "acceptanceChecks", "dependsOn", "deterministic"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["status", "summary", "tasks"],
          "additionalProperties": false
        }
        """;

    private const string PlannerSchema =
        """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "tasks": {
              "type": "array",
              "maxItems": 100,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "title": { "type": "string" },
                  "executor": {
                    "type": "string",
                    "enum": ["deterministic", "luna_low"]
                  },
                  "prompt": { "type": "string" },
                  "readFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "writeFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "acceptanceChecks": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "dependsOn": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "deterministic": {
                    "type": "object",
                    "properties": {
                      "kind": {
                        "type": "string",
                        "enum": ["none", "write_file", "create_directory", "rename_path", "run_command"]
                      },
                      "path": { "type": "string" },
                      "content": { "type": "string" },
                      "sourcePath": { "type": "string" },
                      "destinationPath": { "type": "string" },
                      "executable": { "type": "string" },
                      "arguments": {
                        "type": "array",
                        "items": { "type": "string" }
                      },
                      "workingDirectory": { "type": "string" },
                      "timeoutSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 900
                      }
                    },
                    "required": [
                      "kind", "path", "content", "sourcePath",
                      "destinationPath", "executable", "arguments",
                      "workingDirectory", "timeoutSeconds"
                    ],
                    "additionalProperties": false
                  }
                },
                "required": [
                  "id", "title", "executor", "prompt", "readFiles",
                  "writeFiles", "acceptanceChecks", "dependsOn", "deterministic"
                ],
                "additionalProperties": false
              }
            },
            "finalChecks": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["summary", "tasks", "finalChecks"],
          "additionalProperties": false
        }
        """;
}
