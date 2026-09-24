using LoopGolem.Core.Domain;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Orchestrator;

public sealed class MissionOrchestrator : IMissionOrchestrator
{
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
        var tasks = CreatePlan(missionId, executionMode, now);

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
            tasks);

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

                var currentTask = snapshot.Tasks
                    .OrderBy(task => task.Sequence)
                    .FirstOrDefault(task => task.Status != DomainTaskStatus.Completed);

                if (currentTask is null)
                {
                    var completedAt = DateTimeOffset.UtcNow;
                    snapshot = snapshot with
                    {
                        Mission = snapshot.Mission with
                        {
                            Status = MissionStatus.Completed,
                            Result = BuildMissionResult(snapshot.Tasks),
                            Error = null,
                            UpdatedAtUtc = completedAt
                        }
                    };
                    await _store.UpdateAsync(snapshot, cancellationToken);
                    return snapshot;
                }

                if (!_executors.TryGetValue(currentTask.Kind, out var executor))
                {
                    return await MarkMissionFailedAsync(
                        snapshot,
                        $"No executor is registered for task kind '{currentTask.Kind}'.",
                        cancellationToken);
                }

                var startedAt = DateTimeOffset.UtcNow;
                var runningTask = currentTask with
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
                            Status = MissionStatus.Running,
                            Error = null,
                            UpdatedAtUtc = startedAt
                        }
                    },
                    runningTask);

                await _store.UpdateAsync(snapshot, cancellationToken);

                try
                {
                    var result = await executor.ExecuteAsync(
                        snapshot.Mission,
                        runningTask,
                        cancellationToken);

                    var finishedAt = DateTimeOffset.UtcNow;
                    if (!result.Success)
                    {
                        var failedTask = runningTask with
                        {
                            Status = DomainTaskStatus.Failed,
                            Result = result.Summary,
                            ResultDetails = result.Details,
                            Error = result.Error ?? result.Summary,
                            UpdatedAtUtc = finishedAt
                        };

                        snapshot = ReplaceTask(
                            snapshot with
                            {
                                Mission = snapshot.Mission with
                                {
                                    Status = MissionStatus.Failed,
                                    Error = failedTask.Error,
                                    UpdatedAtUtc = finishedAt
                                }
                            },
                            failedTask);

                        await _store.UpdateAsync(snapshot, cancellationToken);
                        return snapshot;
                    }

                    snapshot = ReplaceTask(
                        snapshot,
                        runningTask with
                        {
                            Status = DomainTaskStatus.Completed,
                            Result = result.Summary,
                            ResultDetails = result.Details,
                            Error = null,
                            UpdatedAtUtc = finishedAt
                        });

                    var nextTask = snapshot.Tasks
                        .OrderBy(task => task.Sequence)
                        .FirstOrDefault(task => task.Status != DomainTaskStatus.Completed);

                    if (nextTask is not null &&
                        nextTask.Status == DomainTaskStatus.Planned)
                    {
                        snapshot = ReplaceTask(
                            snapshot,
                            nextTask with
                            {
                                Status = DomainTaskStatus.Ready,
                                UpdatedAtUtc = finishedAt
                            });
                    }

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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return await MarkMissionFailedAsync(
                        ReplaceTask(
                            snapshot,
                            runningTask with
                            {
                                Status = DomainTaskStatus.Failed,
                                Error = exception.Message,
                                UpdatedAtUtc = DateTimeOffset.UtcNow
                            }),
                        exception.Message,
                        cancellationToken);
                }
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

    private static IReadOnlyList<MissionTask> CreatePlan(
        string missionId,
        MissionExecutionMode executionMode,
        DateTimeOffset now)
    {
        var plan = new List<(MissionTaskKind Kind, string Title)>
        {
            (MissionTaskKind.InspectWorkspace, "Inspect workspace"),
            (MissionTaskKind.DiscoverProjects, "Discover project files")
        };

        if (executionMode == MissionExecutionMode.Codex)
        {
            plan.Add((MissionTaskKind.AgentWork, "Execute goal with Codex"));
            plan.Add((MissionTaskKind.InspectGitChanges, "Inspect Git changes"));
        }

        plan.Add((MissionTaskKind.BuildDotNet, "Build .NET workspace"));

        return plan.Select((step, index) => new MissionTask(
            Guid.NewGuid().ToString("N"),
            missionId,
            index + 1,
            step.Kind,
            step.Title,
            index == 0 ? DomainTaskStatus.Ready : DomainTaskStatus.Planned,
            null,
            null,
            null,
            now,
            now)).ToArray();
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

    private static string BuildMissionResult(IEnumerable<MissionTask> tasks) =>
        string.Join(
            Environment.NewLine,
            tasks
                .OrderBy(task => task.Sequence)
                .Where(task => !string.IsNullOrWhiteSpace(task.Result))
                .Select(task => $"{task.Title}: {task.Result}"));
}
