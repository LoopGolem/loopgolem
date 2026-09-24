using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed class CodexPlanningService(
    ProcessRunner processRunner,
    CodexCliService runtime)
{
    public const string PlannerModel = "gpt-6-luna";
    public const string PlannerReasoning = "high";
    public const string WorkerModel = "gpt-6-luna";
    public const string WorkerReasoning = "low";

    private static readonly TimeSpan ExecutionTimeout =
        TimeSpan.FromMinutes(60);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly WslRuntimeService _wsl = new(processRunner);

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

        var run = await RunStructuredAsync(
            mission.WorkspacePath,
            PlannerModel,
            PlannerReasoning,
            "read-only",
            PlannerSchema,
            BuildPlannerPrompt(mission),
            cancellationToken);

        if (run.Process.TimedOut)
        {
            return TaskExecutionResult.Failed(
                "Planner timed out.",
                "GPT-6 Luna High exceeded the planner timeout.",
                run.Details);
        }

        if (run.Process.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                $"Planner exited with code {run.Process.ExitCode}.",
                GetProcessError(run.Process),
                run.Details);
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
                run.Details);
        }

        if (plan is null)
        {
            return TaskExecutionResult.Failed(
                "Planner returned no mission plan.",
                "The structured planner response was empty.",
                run.Details);
        }

        var validationError = MissionPlanValidator.Validate(plan);
        if (validationError is not null)
        {
            return TaskExecutionResult.Failed(
                "Planner returned an invalid mission plan.",
                validationError,
                run.Details);
        }

        return TaskExecutionResult.Succeeded(
            plan.Summary,
            JsonSerializer.Serialize(plan, JsonOptions));
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

        GitWorkspaceSnapshot before;
        try
        {
            before = await GitWorkspaceSnapshot.CaptureAsync(
                processRunner,
                mission.WorkspacePath,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return TaskExecutionResult.Failed(
                "Could not capture the workspace before the microtask.",
                exception.Message);
        }

        var run = await RunStructuredAsync(
            mission.WorkspacePath,
            WorkerModel,
            WorkerReasoning,
            "workspace-write",
            WorkerSchema,
            BuildWorkerPrompt(definition),
            cancellationToken);

        if (run.Process.TimedOut)
        {
            return TaskExecutionResult.Failed(
                "Luna Low microtask timed out.",
                "The microtask exceeded the one-hour timeout.",
                run.Details);
        }

        if (run.Process.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                $"Luna Low exited with code {run.Process.ExitCode}.",
                GetProcessError(run.Process),
                run.Details);
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
                run.Details);
        }

        if (outcome is null)
        {
            return TaskExecutionResult.Failed(
                "Luna Low returned no structured output.",
                "The final response was empty.",
                run.Details);
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
                run.Details);
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
                run.Details);
        }

        return outcome.Outcome switch
        {
            "blocked" => TaskExecutionResult.Failed(
                outcome.Summary,
                string.IsNullOrWhiteSpace(outcome.Blocker)
                    ? "Luna Low reported a blocker."
                    : outcome.Blocker,
                run.Details),
            "changed" when changedByTask.Count == 0 =>
                TaskExecutionResult.Failed(
                    "Luna Low reported changes, but no file changed.",
                    "The structured result disagrees with deterministic Git verification.",
                    run.Details),
            "changed" => TaskExecutionResult.Succeeded(
                outcome.Summary,
                run.Details),
            "already_satisfied" when changedByTask.Count > 0 =>
                TaskExecutionResult.Failed(
                    "Luna Low reported no edit was needed, but files changed.",
                    string.Join(", ", changedByTask),
                    run.Details),
            "already_satisfied" => TaskExecutionResult.Succeeded(
                outcome.Summary,
                run.Details),
            _ => TaskExecutionResult.Failed(
                "Luna Low returned an unsupported outcome.",
                outcome.Outcome,
                run.Details)
        };
    }

    private async Task<(ProcessRunResult Process, string FinalMessage, string Details)>
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

            var details = JsonSerializer.Serialize(new
            {
                model,
                reasoning,
                sandbox,
                process
            });

            return (process, finalMessage.Trim(), details);
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

    private static string BuildPlannerPrompt(Mission mission) =>
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
        - finalChecks lists repository-level checks for the future high-reasoning validator.
        """;

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
