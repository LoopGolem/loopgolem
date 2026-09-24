using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;

namespace LoopGolem.Worker.Execution;

public sealed record ProjectDiscoveryData(
    IReadOnlyList<string> ProjectFiles);

public sealed class ProjectDiscoveryExecutor : IMissionTaskExecutor
{
    private static readonly HashSet<string> IgnoredDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".loopgolem",
            "bin",
            "obj"
        };

    public MissionTaskKind Kind => MissionTaskKind.DiscoverProjects;

    public Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(mission.WorkspacePath))
        {
            throw new DirectoryNotFoundException(
                $"Workspace '{mission.WorkspacePath}' does not exist.");
        }

        var projectFiles = new List<string>();
        var pending = new Stack<string>();
        pending.Push(mission.WorkspacePath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<string> childDirectories;
            IEnumerable<string> files;

            try
            {
                childDirectories = Directory.EnumerateDirectories(directory);
                files = Directory.EnumerateFiles(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                var name = Path.GetFileName(childDirectory);
                if (!IgnoredDirectoryNames.Contains(name))
                {
                    pending.Push(childDirectory);
                }
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(file);
                if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    projectFiles.Add(
                        Path.GetRelativePath(mission.WorkspacePath, file));
                }
            }
        }

        projectFiles.Sort(StringComparer.OrdinalIgnoreCase);
        var data = new ProjectDiscoveryData(projectFiles);

        if (projectFiles.Count == 0)
        {
            return Task.FromResult(
                TaskExecutionResult.Succeeded(
                    "No .NET solution or project files were found.",
                    JsonSerializer.Serialize(data)));
        }

        return Task.FromResult(
            TaskExecutionResult.Succeeded(
                $"Found {projectFiles.Count} .NET solution/project files: " +
                string.Join(", ", projectFiles),
                JsonSerializer.Serialize(data)));
    }
}
