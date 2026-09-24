namespace LoopGolem.Core.Domain;

public static class PlannedExecutorKinds
{
    public const string Internal = "internal";
    public const string Deterministic = "deterministic";
    public const string LunaLow = "luna_low";
}

public static class DeterministicOperationKinds
{
    public const string None = "none";
    public const string WriteFile = "write_file";
    public const string CreateDirectory = "create_directory";
    public const string RenamePath = "rename_path";
    public const string RunCommand = "run_command";
}

public sealed record DeterministicOperation(
    string Kind,
    string Path,
    string Content,
    string SourcePath,
    string DestinationPath,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int TimeoutSeconds);

public sealed record PlannedTask(
    string Id,
    string Title,
    string Executor,
    string Prompt,
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> WriteFiles,
    IReadOnlyList<string> AcceptanceChecks,
    IReadOnlyList<string> DependsOn,
    DeterministicOperation Deterministic);

public sealed record MissionPlan(
    string Summary,
    IReadOnlyList<PlannedTask> Tasks,
    IReadOnlyList<string> FinalChecks);

public sealed record PlannerResult(
    string BaseCommit,
    MissionPlan Plan);

public sealed record ValidationResult(
    string Status,
    string Summary,
    IReadOnlyList<PlannedTask> Tasks,
    string SnapshotCommit);

public static class MissionPlanValidator
{
    private static readonly HashSet<string> ReservedIds =
    [
        "inspect-workspace",
        "discover-projects",
        "plan-mission",
        "inspect-git",
        "build-dotnet",
        "validate-mission"
    ];

    public static string? Validate(MissionPlan plan) =>
        ValidateTasks(plan.Tasks);

    public static string? ValidateTasks(IReadOnlyList<PlannedTask> tasks)
    {
        if (tasks.Count > 100)
        {
            return "Planner returned more than 100 microtasks.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id) ||
                ReservedIds.Contains(task.Id) ||
                !ids.Add(task.Id))
            {
                return $"Planner returned an invalid or duplicate task id '{task.Id}'.";
            }

            if (string.IsNullOrWhiteSpace(task.Title))
            {
                return $"Task '{task.Id}' has no title.";
            }

            if (task.Executor is not (
                    PlannedExecutorKinds.Deterministic or
                    PlannedExecutorKinds.LunaLow))
            {
                return $"Task '{task.Id}' uses unsupported executor '{task.Executor}'.";
            }

            foreach (var path in task.ReadFiles.Concat(task.WriteFiles))
            {
                if (!IsSafeRelativePath(path))
                {
                    return $"Task '{task.Id}' contains unsafe repository path '{path}'.";
                }
            }

            if (task.Executor == PlannedExecutorKinds.LunaLow &&
                string.IsNullOrWhiteSpace(task.Prompt))
            {
                return $"Luna Low task '{task.Id}' has an empty prompt.";
            }

            var deterministicError = ValidateDeterministic(task);
            if (deterministicError is not null)
            {
                return deterministicError;
            }
        }

        foreach (var task in tasks)
        {
            foreach (var dependency in task.DependsOn)
            {
                if (dependency == task.Id || !ids.Contains(dependency))
                {
                    return $"Task '{task.Id}' has invalid dependency '{dependency}'.";
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var byId = tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (HasCycle(task.Id, byId, visiting, visited))
            {
                return "Planner returned a dependency cycle.";
            }
        }

        return null;
    }

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        return !path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");
    }

    private static string? ValidateDeterministic(PlannedTask task)
    {
        var operation = task.Deterministic;

        if (task.Executor == PlannedExecutorKinds.LunaLow)
        {
            return operation.Kind == DeterministicOperationKinds.None
                ? null
                : $"Luna Low task '{task.Id}' must use deterministic kind 'none'.";
        }

        if (operation.Kind == DeterministicOperationKinds.None)
        {
            return $"Deterministic task '{task.Id}' is missing an operation.";
        }

        if (operation.TimeoutSeconds is < 1 or > 900)
        {
            return $"Task '{task.Id}' has invalid deterministic timeout.";
        }

        return operation.Kind switch
        {
            DeterministicOperationKinds.WriteFile
                when !IsSafeRelativePath(operation.Path) =>
                $"Task '{task.Id}' has an unsafe write_file path.",
            DeterministicOperationKinds.CreateDirectory
                when !IsSafeRelativePath(operation.Path) =>
                $"Task '{task.Id}' has an unsafe create_directory path.",
            DeterministicOperationKinds.RenamePath
                when !IsSafeRelativePath(operation.SourcePath) ||
                     !IsSafeRelativePath(operation.DestinationPath) =>
                $"Task '{task.Id}' has unsafe rename paths.",
            DeterministicOperationKinds.RunCommand
                when string.IsNullOrWhiteSpace(operation.Executable) =>
                $"Task '{task.Id}' has no executable.",
            DeterministicOperationKinds.RunCommand
                when !string.IsNullOrWhiteSpace(operation.WorkingDirectory) &&
                     !IsSafeRelativePath(operation.WorkingDirectory) =>
                $"Task '{task.Id}' has an unsafe command working directory.",
            DeterministicOperationKinds.WriteFile or
            DeterministicOperationKinds.CreateDirectory or
            DeterministicOperationKinds.RenamePath or
            DeterministicOperationKinds.RunCommand => null,
            _ => $"Task '{task.Id}' uses unsupported deterministic operation '{operation.Kind}'."
        };
    }

    private static bool HasCycle(
        string id,
        IReadOnlyDictionary<string, PlannedTask> byId,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        if (visited.Contains(id))
        {
            return false;
        }

        if (!visiting.Add(id))
        {
            return true;
        }

        foreach (var dependency in byId[id].DependsOn)
        {
            if (HasCycle(dependency, byId, visiting, visited))
            {
                return true;
            }
        }

        visiting.Remove(id);
        visited.Add(id);
        return false;
    }
}
