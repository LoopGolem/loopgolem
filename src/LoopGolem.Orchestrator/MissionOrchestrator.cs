using System.Text.Json;
using LoopGolem.Core.Domain;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Orchestrator;

public sealed class MissionOrchestrator : IMissionOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IMissionStore _store;
    private readonly IReadOnlyDictionary<MissionTaskKind, IMissionTaskExecutor> _executors;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public MissionOrchestrator(
        IMissionStore store,
        IEnumerable<IMissionTaskExecutor> executors)
    {
        _store = store;
        var executorArray = executors.ToArray();
        _executors = executorArray.ToDictionary(executor => executor.Kind);

        if (_executors.Count != executorArray.Length)
        {
            throw new ArgumentException(
                "Only one executor may be registered for each mission task kind.",
                nameof(executors));
        }
    }

    public async Task<MissionSnapshot> CreateMissionAsync(
        string goal,
        string workspacePath,
        MissionExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var now = DateTimeOffset.UtcNow;
        var missionId = Guid.NewGuid().ToString("N");
        var snapshot = new MissionSnapshot(
            new Mission(
                missionId,
                goal.Trim(),
                Path.GetFullPath(workspacePath),
                executionMode,
                MissionStatus.Created,
                null,
                null,
                now,
                now),
            CreateInitialPlan(missionId, executionMode, now));

        snapshot = UpdateReadyStates(snapshot, now);
        await _store.CreateAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<MissionSnapshot?> RunMissionAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _store.GetAsync(missionId, cancellationToken);
            if (snapshot is null)
            {
                return null;
            }

            if (snapshot.Mission.Status is MissionStatus.Completed or MissionStatus.Failed)
            {
                return snapshot;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = UpdateReadyStates(snapshot, DateTimeOffset.UtcNow);

                var incomplete = snapshot.Tasks
                    .Where(task => task.Status != DomainTaskStatus.Completed)
                    .OrderBy(task => task.Sequence)
                    .ToArray();

                if (incomplete.Length == 0)
                {
                    snapshot = snapshot with
                    {
                        Mission = snapshot.Mission with
                        {
                            Status = MissionStatus.Completed,
                            Result = BuildMissionResult(snapshot.Tasks),
                            Error = null,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        }
                    };
                    await _store.UpdateAsync(snapshot, cancellationToken);
                    return snapshot;
                }

                var failed = incomplete.FirstOrDefault(
                    task => task.Status == DomainTaskStatus.Failed);
                if (failed is not null)
                {
                    return await MarkMissionFailedAsync(
                        snapshot,
                        failed.Error ?? $"Task '{failed.Title}' failed.",
                        cancellationToken);
                }

                var current = incomplete.FirstOrDefault(
                    task => task.Status is
                        DomainTaskStatus.Ready or
                        DomainTaskStatus.Running or
                        DomainTaskStatus.Retrying);

                if (current is null)
                {
                    return await MarkMissionFailedAsync(
                        snapshot,
                        "No runnable task remains. The plan may contain unresolved dependencies.",
                        cancellationToken);
                }

                if (!_executors.TryGetValue(current.Kind, out var executor))
                {
                    return await MarkMissionFailedAsync(
                        snapshot,
                        $"No executor is registered for task kind '{current.Kind}'.",
                        cancellationToken);
                }

                var startedAt = DateTimeOffset.UtcNow;
                var running = current with
                {
                    Status = DomainTaskStatus.Running,
                    Error = null,
                    UpdatedAtUtc = startedAt
                };

                snapshot = ReplaceTask(
                    snapshot with
                    {
                        Mission = snapshot.Mission with
                        {
                            Status = current.Kind == MissionTaskKind.PlanMission
                                ? MissionStatus.Planning
                                : MissionStatus.Running,
                            Error = null,
                            UpdatedAtUtc = startedAt
                        }
                    },
                    running);
                await _store.UpdateAsync(snapshot, cancellationToken);

                var result = await executor.ExecuteAsync(
                    snapshot.Mission,
                    running,
                    cancellationToken);

                var finishedAt = DateTimeOffset.UtcNow;
                if (!result.Success)
                {
                    var failedTask = running with
                    {
                        Status = DomainTaskStatus.Failed,
                        Result = result.Summary,
                        ResultDetails = result.Details,
                        Error = result.Error ?? result.Summary,
                        UpdatedAtUtc = finishedAt
                    };

                    snapshot = ReplaceTask(snapshot, failedTask);
                    snapshot = snapshot with
                    {
                        Mission = snapshot.Mission with
                        {
                            Status = MissionStatus.Failed,
                            Error = failedTask.Error,
                            UpdatedAtUtc = finishedAt
                        }
                    };

                    await _store.UpdateAsync(snapshot, cancellationToken);
                    return snapshot;
                }

                var completed = running with
                {
                    Status = DomainTaskStatus.Completed,
                    Result = result.Summary,
                    ResultDetails = result.Details,
                    Error = null,
                    UpdatedAtUtc = finishedAt
                };

                snapshot = ReplaceTask(snapshot, completed);

                if (completed.Kind == MissionTaskKind.PlanMission)
                {
                    var expansion = ExpandPlannerResult(
                        snapshot,
                        completed,
                        finishedAt);

                    if (expansion.Error is not null)
                    {
                        return await MarkMissionFailedAsync(
                            snapshot,
                            expansion.Error,
                            cancellationToken);
                    }

                    snapshot = expansion.Snapshot!;
                }

                snapshot = UpdateReadyStates(snapshot, finishedAt);
                snapshot = snapshot with
                {
                    Mission = snapshot.Mission with
                    {
                        Status = MissionStatus.Running,
                        UpdatedAtUtc = finishedAt
                    }
                };

                await _store.UpdateAsync(snapshot, cancellationToken);
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async Task ResumePendingAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await _store.ListRecoverableAsync(cancellationToken);
        foreach (var snapshot in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunMissionAsync(snapshot.Mission.Id, cancellationToken);
        }
    }

    private static IReadOnlyList<MissionTask> CreateInitialPlan(
        string missionId,
        MissionExecutionMode executionMode,
        DateTimeOffset now)
    {
        var steps = new List<(MissionTaskKind Kind, string Title, PlannedTask Definition)>
        {
            (MissionTaskKind.InspectWorkspace, "Inspect workspace",
                InternalDefinition("inspect-workspace", [])),
            (MissionTaskKind.DiscoverProjects, "Discover project files",
                InternalDefinition("discover-projects", ["inspect-workspace"]))
        };

        steps.Add(executionMode == MissionExecutionMode.Codex
            ? (MissionTaskKind.PlanMission, "Plan mission",
                InternalDefinition("plan-mission", ["discover-projects"]))
            : (MissionTaskKind.BuildDotNet, "Build .NET workspace",
                InternalDefinition("build-dotnet", ["discover-projects"])));

        return steps.Select((step, index) => new MissionTask(
            Guid.NewGuid().ToString("N"),
            missionId,
            index + 1,
            step.Kind,
            step.Title,
            step.Definition,
            DomainTaskStatus.Planned,
            null,
            null,
            null,
            now,
            now)).ToArray();
    }

    private static (MissionSnapshot? Snapshot, string? Error) ExpandPlannerResult(
        MissionSnapshot snapshot,
        MissionTask plannerTask,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(plannerTask.ResultDetails))
        {
            return (null, "Planner completed without a structured mission plan.");
        }

        MissionPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<MissionPlan>(
                plannerTask.ResultDetails,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return (null, $"Planner returned invalid JSON: {exception.Message}");
        }

        if (plan is null)
        {
            return (null, "Planner returned an empty mission plan.");
        }

        var planError = MissionPlanValidator.Validate(plan);
        if (planError is not null)
        {
            return (null, planError);
        }

        var tasks = snapshot.Tasks.ToList();
        var sequence = tasks.Max(task => task.Sequence) + 1;

        foreach (var definition in plan.Tasks)
        {
            tasks.Add(new MissionTask(
                Guid.NewGuid().ToString("N"),
                snapshot.Mission.Id,
                sequence++,
                definition.Executor == PlannedExecutorKinds.Deterministic
                    ? MissionTaskKind.DeterministicWork
                    : MissionTaskKind.AgentWork,
                definition.Title,
                definition,
                DomainTaskStatus.Planned,
                null,
                null,
                null,
                now,
                now));
        }

        var terminalDependencies = plan.Tasks.Count == 0
            ? new[] { "plan-mission" }
            : plan.Tasks.Select(task => task.Id).ToArray();

        tasks.Add(new MissionTask(
            Guid.NewGuid().ToString("N"),
            snapshot.Mission.Id,
            sequence++,
            MissionTaskKind.InspectGitChanges,
            "Inspect Git changes",
            InternalDefinition("inspect-git", terminalDependencies),
            DomainTaskStatus.Planned,
            null,
            null,
            null,
            now,
            now));

        tasks.Add(new MissionTask(
            Guid.NewGuid().ToString("N"),
            snapshot.Mission.Id,
            sequence,
            MissionTaskKind.BuildDotNet,
            "Build .NET workspace",
            InternalDefinition("build-dotnet", ["inspect-git"]),
            DomainTaskStatus.Planned,
            null,
            null,
            null,
            now,
            now));

        return (snapshot with { Tasks = tasks.OrderBy(task => task.Sequence).ToArray() }, null);
    }

    private static MissionSnapshot UpdateReadyStates(
        MissionSnapshot snapshot,
        DateTimeOffset now)
    {
        var tasks = snapshot.Tasks.ToArray();
        var changed = false;

        for (var i = 0; i < tasks.Length; i++)
        {
            if (tasks[i].Status != DomainTaskStatus.Planned ||
                !DependenciesSatisfied(tasks[i], tasks))
            {
                continue;
            }

            tasks[i] = tasks[i] with
            {
                Status = DomainTaskStatus.Ready,
                UpdatedAtUtc = now
            };
            changed = true;
        }

        return changed ? snapshot with { Tasks = tasks } : snapshot;
    }

    private static bool DependenciesSatisfied(
        MissionTask task,
        IReadOnlyList<MissionTask> allTasks)
    {
        if (task.Definition is null)
        {
            return allTasks
                .Where(other => other.Sequence < task.Sequence)
                .All(other => other.Status == DomainTaskStatus.Completed);
        }

        foreach (var dependencyId in task.Definition.DependsOn)
        {
            var dependency = allTasks.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Definition?.Id,
                    dependencyId,
                    StringComparison.Ordinal));

            if (dependency is null ||
                dependency.Status != DomainTaskStatus.Completed)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<MissionSnapshot> MarkMissionFailedAsync(
        MissionSnapshot snapshot,
        string error,
        CancellationToken cancellationToken)
    {
        snapshot = snapshot with
        {
            Mission = snapshot.Mission with
            {
                Status = MissionStatus.Failed,
                Error = error,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }
        };
        await _store.UpdateAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private static MissionSnapshot ReplaceTask(
        MissionSnapshot snapshot,
        MissionTask replacement) =>
        snapshot with
        {
            Tasks = snapshot.Tasks
                .Select(task => task.Id == replacement.Id ? replacement : task)
                .OrderBy(task => task.Sequence)
                .ToArray()
        };

    private static PlannedTask InternalDefinition(
        string id,
        IReadOnlyList<string> dependsOn) =>
        new(
            id,
            id,
            PlannedExecutorKinds.Internal,
            string.Empty,
            [],
            [],
            [],
            dependsOn,
            new DeterministicOperation(
                DeterministicOperationKinds.None,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                string.Empty,
                60));

    private static string BuildMissionResult(IEnumerable<MissionTask> tasks) =>
        string.Join(
            Environment.NewLine,
            tasks
                .OrderBy(task => task.Sequence)
                .Where(task => !string.IsNullOrWhiteSpace(task.Result))
                .Select(task => $"{task.Title}: {task.Result}"));
}
