using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Execution;

public sealed record GitChangesDetails(
    ProcessRunResult Status,
    ProcessRunResult DiffCheck,
    ProcessRunResult DiffStat);

public sealed class GitChangesExecutor(ProcessRunner processRunner)
    : IMissionTaskExecutor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public MissionTaskKind Kind => MissionTaskKind.InspectGitChanges;

    public async Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var status = await RunGitAsync(
            mission.WorkspacePath,
            ["status", "--short"],
            cancellationToken);

        if (status.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                "Git status failed.",
                status.StandardError.Trim(),
                JsonSerializer.Serialize(status));
        }

        var diffCheck = await RunGitAsync(
            mission.WorkspacePath,
            ["diff", "--check"],
            cancellationToken);
        var diffStat = await RunGitAsync(
            mission.WorkspacePath,
            ["diff", "--stat"],
            cancellationToken);

        var details = JsonSerializer.Serialize(
            new GitChangesDetails(status, diffCheck, diffStat));

        if (diffCheck.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                "Git diff validation failed.",
                string.IsNullOrWhiteSpace(diffCheck.StandardError)
                    ? diffCheck.StandardOutput.Trim()
                    : diffCheck.StandardError.Trim(),
                details);
        }

        var changed = status.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        return TaskExecutionResult.Succeeded(
            changed.Length == 0
                ? "No uncommitted Git changes were detected."
                : $"Git reports {changed.Length} changed path(s).",
            details);
    }

    private Task<ProcessRunResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        processRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            Timeout,
            cancellationToken);
}
