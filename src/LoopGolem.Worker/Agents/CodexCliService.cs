using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed record CodexExecutionDetails(
    ProcessRunResult Process,
    string? FinalMessage);

public sealed class CodexCliService(ProcessRunner processRunner)
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMinutes(60);

    public async Task<CodexRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await processRunner.RunAsync(
                "codex",
                ["--version"],
                Environment.CurrentDirectory,
                StatusTimeout,
                cancellationToken);

            var login = await processRunner.RunAsync(
                "codex",
                ["login", "status"],
                Environment.CurrentDirectory,
                StatusTimeout,
                cancellationToken);

            var loginText = string.Join(
                Environment.NewLine,
                new[] { login.StandardOutput, login.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            var chatGpt =
                login.ExitCode == 0 &&
                loginText.Contains(
                    "Logged in using ChatGPT",
                    StringComparison.OrdinalIgnoreCase);

            var versionText = version.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(versionText))
            {
                versionText = version.StandardError.Trim();
            }

            return new CodexRuntimeStatus(
                Available: version.ExitCode == 0 && !version.TimedOut,
                ChatGptAuthenticated: chatGpt,
                Version: string.IsNullOrWhiteSpace(versionText) ? null : versionText,
                Message: chatGpt
                    ? "Codex CLI is authenticated with ChatGPT."
                    : loginText.Trim());
        }
        catch (Exception exception)
        {
            return new CodexRuntimeStatus(false, false, null, exception.Message);
        }
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
                "Codex CLI is unavailable.",
                status.Message);
        }

        if (!status.ChatGptAuthenticated)
        {
            return TaskExecutionResult.Failed(
                "Codex is not authenticated with ChatGPT.",
                "LoopGolem refuses agent work unless Codex reports ChatGPT authentication.");
        }

        var runtimeDirectory = Path.Combine(
            Path.GetTempPath(),
            "LoopGolem",
            "codex",
            mission.Id);
        Directory.CreateDirectory(runtimeDirectory);

        var lastMessagePath = Path.Combine(
            runtimeDirectory,
            $"{task.Id}.last-message.txt");

        var args = new[]
        {
            "exec",
            "--ephemeral",
            "--ignore-user-config",
            "--color", "never",
            "--sandbox", "workspace-write",
            "--cd", mission.WorkspacePath,
            "--config", "approval_policy=never",
            "--config", "sandbox_workspace_write.network_access=false",
            "--output-last-message", lastMessagePath,
            "-"
        };

        var run = await processRunner.RunAsync(
            "codex",
            args,
            mission.WorkspacePath,
            ExecutionTimeout,
            cancellationToken,
            BuildPrompt(mission));

        string? finalMessage = null;
        if (File.Exists(lastMessagePath))
        {
            finalMessage = (await File.ReadAllTextAsync(
                lastMessagePath,
                cancellationToken)).Trim();
        }

        var details = JsonSerializer.Serialize(
            new CodexExecutionDetails(run, finalMessage));

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

        return TaskExecutionResult.Succeeded(
            string.IsNullOrWhiteSpace(finalMessage)
                ? "Codex completed the requested work."
                : finalMessage,
            details);
    }

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
        - Network access is disabled.
        - Run useful local checks when they help validate your changes.
        - If the goal is already satisfied, verify it and avoid unnecessary edits.
        - Do not ask the user questions during this task.
        - End with a concise summary of changes and checks performed.
        """;
}
