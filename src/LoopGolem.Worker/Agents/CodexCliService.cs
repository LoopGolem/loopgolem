using System.Text.Json;
using System.Text.Json.Serialization;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed record CodexAgentOutcome(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("checks")] IReadOnlyList<string> Checks,
    [property: JsonPropertyName("blocker")] string Blocker);

public sealed record CodexExecutionDetails(
    ProcessRunResult Process,
    CodexAgentOutcome? Outcome,
    string GitStatusBefore,
    string GitStatusAfter,
    string Runtime,
    string? Distribution,
    string Model,
    string ReasoningEffort);

public sealed class CodexCliService
{
    public const string DefaultModel = "gpt-6-luna";
    public const string DefaultReasoningEffort = "low";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMinutes(60);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ProcessRunner _processRunner;
    private readonly WslRuntimeService _wsl;

    public CodexCliService(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
        _wsl = new WslRuntimeService(processRunner);
    }

    public async Task<CodexRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        return OperatingSystem.IsWindows()
            ? await GetWindowsStatusAsync(cancellationToken)
            : await GetNativeStatusAsync(cancellationToken);
    }

    public async Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
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

        var gitStatusBefore = await GetGitStatusAsync(
            mission.WorkspacePath,
            cancellationToken);

        if (!gitStatusBefore.Success)
        {
            return TaskExecutionResult.Failed(
                "Codex missions require a Git repository.",
                gitStatusBefore.Error);
        }

        if (!string.IsNullOrWhiteSpace(gitStatusBefore.Output))
        {
            return TaskExecutionResult.Failed(
                "Codex mission was not started because the working tree is not clean.",
                "Commit, stash, or discard local changes before starting an autonomous Codex mission.");
        }

        var runtimeDirectory = Path.Combine(
            Path.GetTempPath(),
            "LoopGolem",
            "codex",
            mission.Id);
        Directory.CreateDirectory(runtimeDirectory);

        var lastMessagePath = Path.Combine(
            runtimeDirectory,
            $"{task.Id}.last-message.json");
        var outputSchemaPath = Path.Combine(
            runtimeDirectory,
            $"{task.Id}.output-schema.json");

        await File.WriteAllTextAsync(
            outputSchemaPath,
            OutputSchema,
            cancellationToken);

        ProcessRunResult run;
        string runtimeName;
        string? distribution = null;

        if (OperatingSystem.IsWindows())
        {
            distribution = status.Distribution
                ?? throw new InvalidOperationException(
                    "The WSL distribution disappeared after the Codex status check.");

            var wslWorkspace = await _wsl.ConvertWindowsPathAsync(
                distribution,
                mission.WorkspacePath,
                cancellationToken);
            var wslLastMessage = await _wsl.ConvertWindowsPathAsync(
                distribution,
                lastMessagePath,
                cancellationToken);
            var wslOutputSchema = await _wsl.ConvertWindowsPathAsync(
                distribution,
                outputSchemaPath,
                cancellationToken);

            var codexArguments = BuildCodexArguments(
                wslWorkspace,
                wslOutputSchema,
                wslLastMessage);

            run = await _wsl.RunAsync(
                distribution,
                ["codex", .. codexArguments],
                ExecutionTimeout,
                cancellationToken,
                BuildPrompt(mission));

            runtimeName = "wsl";
        }
        else
        {
            var codexArguments = BuildCodexArguments(
                mission.WorkspacePath,
                outputSchemaPath,
                lastMessagePath);

            run = await _processRunner.RunAsync(
                "codex",
                codexArguments,
                mission.WorkspacePath,
                ExecutionTimeout,
                cancellationToken,
                BuildPrompt(mission));

            runtimeName = "native";
        }

        var gitStatusAfter = await GetGitStatusAsync(
            mission.WorkspacePath,
            cancellationToken);

        CodexAgentOutcome? outcome = null;
        string? outcomeError = null;

        if (File.Exists(lastMessagePath))
        {
            var finalMessage = (await File.ReadAllTextAsync(
                lastMessagePath,
                cancellationToken)).Trim();

            if (!string.IsNullOrWhiteSpace(finalMessage))
            {
                try
                {
                    outcome = JsonSerializer.Deserialize<CodexAgentOutcome>(
                        finalMessage,
                        JsonOptions);
                }
                catch (JsonException exception)
                {
                    outcomeError =
                        $"Codex returned an invalid structured result: {exception.Message}";
                }
            }
        }

        var details = JsonSerializer.Serialize(
            new CodexExecutionDetails(
                run,
                outcome,
                gitStatusBefore.Output,
                gitStatusAfter.Output,
                runtimeName,
                distribution,
                DefaultModel,
                DefaultReasoningEffort),
            JsonOptions);

        TryDelete(lastMessagePath);
        TryDelete(outputSchemaPath);

        if (run.TimedOut)
        {
            return TaskExecutionResult.Failed(
                "Codex execution timed out.",
                "Codex exceeded the one-hour task timeout.",
                details);
        }

        if (run.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(run.StandardError)
                ? run.StandardOutput
                : run.StandardError;

            return TaskExecutionResult.Failed(
                $"Codex exited with code {run.ExitCode}.",
                error.Trim(),
                details);
        }

        if (!gitStatusAfter.Success)
        {
            return TaskExecutionResult.Failed(
                "Codex completed, but LoopGolem could not verify the Git working tree.",
                gitStatusAfter.Error,
                details);
        }

        if (outcome is null)
        {
            return TaskExecutionResult.Failed(
                "Codex did not return a valid LoopGolem outcome.",
                outcomeError ?? "The structured final response was empty or missing.",
                details);
        }

        switch (outcome.Outcome)
        {
            case "blocked":
                return TaskExecutionResult.Failed(
                    outcome.Summary,
                    string.IsNullOrWhiteSpace(outcome.Blocker)
                        ? "Codex reported that it was blocked."
                        : outcome.Blocker,
                    details);

            case "changed":
                if (string.IsNullOrWhiteSpace(gitStatusAfter.Output))
                {
                    return TaskExecutionResult.Failed(
                        "Codex reported changes, but Git detected no working-tree changes.",
                        "LoopGolem will not accept an agent task as successful without deterministic evidence of the requested edit.",
                        details);
                }

                return TaskExecutionResult.Succeeded(
                    outcome.Summary,
                    details);

            case "already_satisfied":
                if (!string.IsNullOrWhiteSpace(gitStatusAfter.Output))
                {
                    return TaskExecutionResult.Failed(
                        "Codex reported that the goal was already satisfied, but Git detected changes.",
                        "The agent result and deterministic Git state disagree.",
                        details);
                }

                return TaskExecutionResult.Succeeded(
                    outcome.Summary,
                    details);

            default:
                return TaskExecutionResult.Failed(
                    "Codex returned an unsupported outcome.",
                    $"Unknown outcome '{outcome.Outcome}'.",
                    details);
        }
    }

    private async Task<CodexRuntimeStatus> GetWindowsStatusAsync(
        CancellationToken cancellationToken)
    {
        var distribution = await _wsl.ResolveDistributionAsync(
            cancellationToken);

        if (!distribution.Success ||
            string.IsNullOrWhiteSpace(distribution.Distribution))
        {
            return new CodexRuntimeStatus(
                Available: false,
                ChatGptAuthenticated: false,
                Version: null,
                Message: distribution.Error ??
                    "No user WSL distribution is available.",
                State: CodexRuntimeState.WslDistributionMissing,
                Runtime: "wsl",
                Distribution: null,
                Model: DefaultModel);
        }

        var distro = distribution.Distribution;

        ProcessRunResult version;
        try
        {
            version = await _wsl.RunAsync(
                distro,
                ["codex", "--version"],
                StatusTimeout,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return new CodexRuntimeStatus(
                false,
                false,
                null,
                exception.Message,
                CodexRuntimeState.CodexCliMissing,
                "wsl",
                distro,
                DefaultModel);
        }

        if (version.TimedOut || version.ExitCode != 0)
        {
            return new CodexRuntimeStatus(
                false,
                false,
                null,
                "Codex CLI is not installed or could not be started inside the selected WSL distribution.",
                CodexRuntimeState.CodexCliMissing,
                "wsl",
                distro,
                DefaultModel);
        }

        var login = await _wsl.RunAsync(
            distro,
            ["codex", "login", "status"],
            StatusTimeout,
            cancellationToken);

        var loginText = JoinProcessText(login);
        var chatGpt = IsChatGptLogin(login, loginText);

        var versionText = version.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(versionText))
        {
            versionText = version.StandardError.Trim();
        }

        return new CodexRuntimeStatus(
            Available: true,
            ChatGptAuthenticated: chatGpt,
            Version: string.IsNullOrWhiteSpace(versionText)
                ? null
                : versionText,
            Message: chatGpt
                ? $"Codex is ready inside WSL distribution '{distro}' using ChatGPT authentication."
                : "Codex CLI is installed in WSL but is not authenticated with ChatGPT. Run 'codex' inside that WSL distribution and choose Sign in with ChatGPT.",
            State: chatGpt
                ? CodexRuntimeState.Ready
                : CodexRuntimeState.AuthenticationRequired,
            Runtime: "wsl",
            Distribution: distro,
            Model: DefaultModel);
    }

    private async Task<CodexRuntimeStatus> GetNativeStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var version = await _processRunner.RunAsync(
                "codex",
                ["--version"],
                Environment.CurrentDirectory,
                StatusTimeout,
                cancellationToken);

            if (version.TimedOut || version.ExitCode != 0)
            {
                return new CodexRuntimeStatus(
                    false,
                    false,
                    null,
                    "Codex CLI could not be started.",
                    CodexRuntimeState.CodexCliMissing,
                    "native",
                    null,
                    DefaultModel);
            }

            var login = await _processRunner.RunAsync(
                "codex",
                ["login", "status"],
                Environment.CurrentDirectory,
                StatusTimeout,
                cancellationToken);

            var loginText = JoinProcessText(login);
            var chatGpt = IsChatGptLogin(login, loginText);

            var versionText = version.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(versionText))
            {
                versionText = version.StandardError.Trim();
            }

            return new CodexRuntimeStatus(
                Available: true,
                ChatGptAuthenticated: chatGpt,
                Version: string.IsNullOrWhiteSpace(versionText)
                    ? null
                    : versionText,
                Message: chatGpt
                    ? "Codex CLI is authenticated with ChatGPT."
                    : loginText.Trim(),
                State: chatGpt
                    ? CodexRuntimeState.Ready
                    : CodexRuntimeState.AuthenticationRequired,
                Runtime: "native",
                Distribution: null,
                Model: DefaultModel);
        }
        catch (Exception exception)
        {
            return new CodexRuntimeStatus(
                false,
                false,
                null,
                exception.Message,
                CodexRuntimeState.CodexCliMissing,
                "native",
                null,
                DefaultModel);
        }
    }

    private static IReadOnlyList<string> BuildCodexArguments(
        string workspacePath,
        string outputSchemaPath,
        string lastMessagePath) =>
        [
            "exec",
            "--ephemeral",
            "--ignore-user-config",
            "--disable", "apps",
            "--disable", "plugins",
            "--disable", "multi_agent",
            "--color", "never",
            "--sandbox", "workspace-write",
            "--cd", workspacePath,
            "--model", DefaultModel,
            "--config", $"model_reasoning_effort=\"{DefaultReasoningEffort}\"",
            "--config", "approval_policy=never",
            "--config", "sandbox_workspace_write.network_access=false",
            "--output-schema", outputSchemaPath,
            "--output-last-message", lastMessagePath,
            "-"
        ];

    private async Task<(bool Success, string Output, string Error)> GetGitStatusAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var run = await _processRunner.RunAsync(
            "git",
            ["status", "--porcelain=v1", "--untracked-files=all"],
            workspacePath,
            GitTimeout,
            cancellationToken);

        if (run.TimedOut)
        {
            return (false, string.Empty, "git status timed out.");
        }

        if (run.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(run.StandardError)
                ? run.StandardOutput
                : run.StandardError;

            return (false, string.Empty, error.Trim());
        }

        return (true, run.StandardOutput.Trim(), string.Empty);
    }

    private static string JoinProcessText(ProcessRunResult run) =>
        string.Join(
            Environment.NewLine,
            new[] { run.StandardOutput, run.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static bool IsChatGptLogin(
        ProcessRunResult login,
        string loginText) =>
        login.ExitCode == 0 &&
        !login.TimedOut &&
        loginText.Contains(
            "Logged in using ChatGPT",
            StringComparison.OrdinalIgnoreCase);

    private static string BuildPrompt(Mission mission) =>
        $"""
        You are a coding worker executing one bounded LoopGolem mission.

        USER GOAL:
        {mission.Goal}

        RULES:
        - Work only inside the current workspace.
        - Inspect the repository and implement the goal directly in the working tree.
        - Do not commit, push, create branches, or rewrite Git history.
        - Do not modify unrelated files.
        - Network access is disabled by LoopGolem.
        - Run useful local checks when they help validate your changes.
        - If the goal is already satisfied, verify it and avoid unnecessary edits.
        - Do not ask the user questions during this task.
        - Report outcome "changed" only if you actually changed the working tree.
        - Report outcome "already_satisfied" only if no edit was required.
        - Report outcome "blocked" if permissions, missing tools, or another blocker prevented completion.
        - Keep the summary concise and list the checks you actually performed.
        """;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private const string OutputSchema =
        """
        {
          "type": "object",
          "properties": {
            "outcome": {
              "type": "string",
              "enum": ["changed", "already_satisfied", "blocked"]
            },
            "summary": {
              "type": "string"
            },
            "checks": {
              "type": "array",
              "items": {
                "type": "string"
              }
            },
            "blocker": {
              "type": "string"
            }
          },
          "required": ["outcome", "summary", "checks", "blocker"],
          "additionalProperties": false
        }
        """;
}
