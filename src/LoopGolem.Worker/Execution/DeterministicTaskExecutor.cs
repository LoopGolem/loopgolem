using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Execution;

public sealed class DeterministicTaskExecutor(
    ProcessRunner processRunner) : IMissionTaskExecutor
{
    public MissionTaskKind Kind => MissionTaskKind.DeterministicWork;

    public async Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var definition = task.Definition;
        if (definition is null ||
            definition.Executor != PlannedExecutorKinds.Deterministic)
        {
            return TaskExecutionResult.Failed(
                "Deterministic task definition is missing.",
                "The planner did not provide a valid deterministic task definition.");
        }

        var op = definition.Deterministic;

        try
        {
            return op.Kind switch
            {
                DeterministicOperationKinds.WriteFile =>
                    await WriteFileAsync(mission, op, cancellationToken),
                DeterministicOperationKinds.CreateDirectory =>
                    CreateDirectory(mission, op),
                DeterministicOperationKinds.RenamePath =>
                    RenamePath(mission, op),
                DeterministicOperationKinds.RunCommand =>
                    await RunCommandAsync(mission, op, cancellationToken),
                _ => TaskExecutionResult.Failed(
                    "Unsupported deterministic operation.",
                    $"Operation '{op.Kind}' is not supported.")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TaskExecutionResult.Failed(
                $"Deterministic operation '{op.Kind}' failed.",
                exception.Message);
        }
    }

    private static async Task<TaskExecutionResult> WriteFileAsync(
        Mission mission,
        DeterministicOperation op,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(mission.WorkspacePath, op.Path);
        var parent = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
        {
            return TaskExecutionResult.Failed(
                "write_file parent directory does not exist.",
                "Add a create_directory task and make write_file depend on it.");
        }

        await File.WriteAllTextAsync(path, op.Content, cancellationToken);
        return TaskExecutionResult.Succeeded($"Wrote '{op.Path}'.");
    }

    private static TaskExecutionResult CreateDirectory(
        Mission mission,
        DeterministicOperation op)
    {
        Directory.CreateDirectory(ResolvePath(mission.WorkspacePath, op.Path));
        return TaskExecutionResult.Succeeded($"Created directory '{op.Path}'.");
    }

    private static TaskExecutionResult RenamePath(
        Mission mission,
        DeterministicOperation op)
    {
        var source = ResolvePath(mission.WorkspacePath, op.SourcePath);
        var destination = ResolvePath(mission.WorkspacePath, op.DestinationPath);
        var parent = Path.GetDirectoryName(destination);

        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
        {
            return TaskExecutionResult.Failed(
                "rename_path destination directory does not exist.",
                "Create the destination directory in a dependency task first.");
        }

        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            return TaskExecutionResult.Failed(
                "rename_path source does not exist.",
                op.SourcePath);
        }

        return TaskExecutionResult.Succeeded(
            $"Renamed '{op.SourcePath}' to '{op.DestinationPath}'.");
    }

    private async Task<TaskExecutionResult> RunCommandAsync(
        Mission mission,
        DeterministicOperation op,
        CancellationToken cancellationToken)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(op.WorkingDirectory)
            ? mission.WorkspacePath
            : ResolvePath(mission.WorkspacePath, op.WorkingDirectory);

        if (!Directory.Exists(workingDirectory))
        {
            return TaskExecutionResult.Failed(
                "run_command working directory does not exist.",
                op.WorkingDirectory);
        }

        var result = await processRunner.RunAsync(
            op.Executable,
            op.Arguments,
            workingDirectory,
            TimeSpan.FromSeconds(op.TimeoutSeconds),
            cancellationToken);

        var details = JsonSerializer.Serialize(result);

        if (result.TimedOut)
        {
            return TaskExecutionResult.Failed(
                $"Command '{op.Executable}' timed out.",
                $"Timeout: {op.TimeoutSeconds} seconds.",
                details);
        }

        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            return TaskExecutionResult.Failed(
                $"Command '{op.Executable}' exited with code {result.ExitCode}.",
                error.Trim(),
                details);
        }

        return TaskExecutionResult.Succeeded(
            $"Command '{op.Executable}' completed successfully.",
            details);
    }

    private static string ResolvePath(string workspaceRoot, string relativePath)
    {
        if (!MissionPlanValidator.IsSafeRelativePath(relativePath))
        {
            throw new InvalidOperationException(
                $"Unsafe repository-relative path '{relativePath}'.");
        }

        var root = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path '{relativePath}' escapes the workspace.");
        }

        return candidate;
    }
}
