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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var now = DateTimeOffset.UtcNow;
        var missionId = Guid.NewGuid().ToString("N");

        var tasks = new[]
        {
            CreateTask(
                missionId,
                1,
                MissionTaskKind.InspectWorkspace,
                "Inspect workspace",
                DomainTaskStatus.Ready,
                now),
            CreateTask(
                missionId,
                2,
                MissionTaskKind.DiscoverProjects,
                "Discover project files",
                DomainTaskStatus.Planned,
                now),
            CreateTask(
                missionId,
                3,
                MissionTaskKind.BuildDotNet,
                "Build .NET workspace",
                DomainTaskStatus.Planned,
                now)
        };

        var snapshot = new MissionSnapshot(
            new Mission(
                missionId,
                goal.Trim(),
                Path.GetFullPath(workspacePath),
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

                if (currentTask.Status == DomainTaskStatus.Failed)
                {
                    return await MarkMissionFailedAsync(
                        snapshot,
                        currentTask.Error ?? "A mission task failed.",
                        cancellationToken);
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
                    var executionResult = await executor.ExecuteAsync(
                        snapshot.Mission,
                        runningTask,
                        cancellationToken);

                    var finishedAt = DateTimeOffset.UtcNow;

                    if (!executionResult.Success)
                    {
                        var failedTask = runningTask with
                        {
                            Status = DomainTaskStatus.Failed,
                            Result = executionResult.Summary,
                            ResultDetails = executionResult.Details,
                            Error = executionResult.Error ?? executionResult.Summary,
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

                    var completedTask = runningTask with
                    {
                        Status = DomainTaskStatus.Completed,
                        Result = executionResult.Summary,
                        ResultDetails = executionResult.Details,
                        Error = null,
                        UpdatedAtUtc = finishedAt
                    };

                    snapshot = ReplaceTask(snapshot, completedTask);

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
                    // Persisted Running state is intentional. Startup recovery
                    // re-runs deterministic tasks that were interrupted.
                    throw;
                }
                catch (Exception exception)
                {
                    var failedAt = DateTimeOffset.UtcNow;
                    var failedTask = runningTask with
                    {
                        Status = DomainTaskStatus.Failed,
                        Error = exception.Message,
                        UpdatedAtUtc = failedAt
                    };

                    snapshot = ReplaceTask(
                        snapshot with
                        {
                            Mission = snapshot.Mission with
                            {
                                Status = MissionStatus.Failed,
                                Error = exception.Message,
                                UpdatedAtUtc = failedAt
                            }
                        },
                        failedTask);

                    await _store.UpdateAsync(snapshot, cancellationToken);
                    return snapshot;
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

    private async Task<MissionSnapshot> MarkMissionFailedAsync(
        MissionSnapshot snapshot,
        string error,
        CancellationToken cancellationToken)
    {
        var failedAt = DateTimeOffset.UtcNow;
        snapshot = snapshot with
        {
            Mission = snapshot.Mission with
            {
                Status = MissionStatus.Failed,
                Error = error,
                UpdatedAtUtc = failedAt
            }
        };

        await _store.UpdateAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private static MissionTask CreateTask(
        string missionId,
        int sequence,
        MissionTaskKind kind,
        string title,
        DomainTaskStatus status,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid().ToString("N"),
            missionId,
            sequence,
            kind,
            title,
            status,
            null,
            null,
            null,
            now,
            now);

    private static MissionSnapshot ReplaceTask(
        MissionSnapshot snapshot,
        MissionTask replacement)
    {
        var tasks = snapshot.Tasks
            .Select(task => task.Id == replacement.Id ? replacement : task)
            .OrderBy(task => task.Sequence)
            .ToArray();

        return snapshot with { Tasks = tasks };
    }

    private static string BuildMissionResult(
        IEnumerable<MissionTask> tasks) =>
        string.Join(
            Environment.NewLine,
            tasks
                .OrderBy(task => task.Sequence)
                .Where(task => !string.IsNullOrWhiteSpace(task.Result))
                .Select(task => $"{task.Title}: {task.Result}"));
}
