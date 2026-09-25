using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Execution;

public sealed class DotNetBuildExecutor(
    ProcessRunner processRunner) : IMissionTaskExecutor
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    public MissionTaskKind Kind => MissionTaskKind.BuildDotNet;

    public async Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var target = FindBuildTarget(mission.WorkspacePath);
        if (target is null)
        {
            return TaskExecutionResult.Succeeded(
                "No root .NET solution or project file was found; build skipped.");
        }

        var relativeTarget = Path.GetRelativePath(
            mission.WorkspacePath,
            target);

        var run = await processRunner.RunAsync(
            "dotnet",
            [
                "build",
                relativeTarget,
                "--configuration",
                "Release",
                "--nologo"
            ],
            mission.WorkspacePath,
            BuildTimeout,
            cancellationToken);

        var details = JsonSerializer.Serialize(run);

        if (run.TimedOut)
        {
            return TaskExecutionResult.Failed(
                $"dotnet build timed out after {BuildTimeout.TotalMinutes:F0} minutes.",
                "The .NET build exceeded its timeout.",
                details,
                failureKind: TaskFailureKind.DeterministicCheck);
        }

        if (run.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                $"dotnet build failed with exit code {run.ExitCode}.",
                GetFailureMessage(run),
                details,
                failureKind: TaskFailureKind.DeterministicCheck);
        }

        return TaskExecutionResult.Succeeded(
            $"dotnet build succeeded for {relativeTarget} (exit code 0).",
            details);
    }

    private static string? FindBuildTarget(string workspacePath)
    {
        var solutions = Directory
            .EnumerateFiles(workspacePath, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                workspacePath,
                "*.slnx",
                SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (solutions.Length == 1)
        {
            return solutions[0];
        }

        if (solutions.Length > 1)
        {
            throw new InvalidOperationException(
                "Multiple root solution files were found. " +
                "The build target must be explicit.");
        }

        var projects = Directory
            .EnumerateFiles(workspacePath, "*.csproj", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                workspacePath,
                "*.fsproj",
                SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(
                workspacePath,
                "*.vbproj",
                SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return projects.Length switch
        {
            0 => null,
            1 => projects[0],
            _ => throw new InvalidOperationException(
                "Multiple root project files were found. " +
                "The build target must be explicit.")
        };
    }

    private static string GetFailureMessage(ProcessRunResult run)
    {
        var diagnostic = string.IsNullOrWhiteSpace(run.StandardError)
            ? run.StandardOutput
            : run.StandardError;

        diagnostic = diagnostic.Trim();

        if (diagnostic.Length > 2000)
        {
            diagnostic = diagnostic[..2000] + Environment.NewLine + "...";
        }

        return string.IsNullOrWhiteSpace(diagnostic)
            ? $"dotnet build exited with code {run.ExitCode}."
            : diagnostic;
    }
}
