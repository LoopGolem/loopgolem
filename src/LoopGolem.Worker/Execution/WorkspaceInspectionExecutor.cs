using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;

namespace LoopGolem.Worker.Execution;

public sealed class WorkspaceInspectionExecutor : IMissionTaskExecutor
{
    private static readonly HashSet<string> IgnoredDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".loopgolem",
            "bin",
            "obj"
        };

    public Task<string> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(mission.WorkspacePath))
        {
            throw new DirectoryNotFoundException(
                $"Workspace '{mission.WorkspacePath}' does not exist.");
        }

        long totalBytes = 0;
        var fileCount = 0;
        var directoryCount = 0;

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
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(childDirectory);
                if (IgnoredDirectoryNames.Contains(name))
                {
                    continue;
                }

                directoryCount++;
                pending.Push(childDirectory);
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileCount++;

                try
                {
                    totalBytes += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // A file may disappear while the workspace is being inspected.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep the inspection useful even if one file is unreadable.
                }
            }
        }

        var sizeMiB = totalBytes / (1024d * 1024d);
        return Task.FromResult(
            $"Workspace inspection complete: {fileCount} files, " +
            $"{directoryCount} directories, {sizeMiB:F2} MiB.");
    }
}
