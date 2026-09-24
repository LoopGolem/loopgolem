using LoopGolem.Core.Domain;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Orchestrator;

public sealed class MissionOrchestrator(
    IMissionStore store,
    IMissionTaskExecutor executor) : IMissionOrchestrator
{
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public async Task<MissionSnapshot> CreateMissionAsync(
        string goal,
        string workspacePath,
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
                MissionStatus.Created,
                null,
                null,
                now,
                now),
            new MissionTask(
                Guid.NewGuid().ToString("N"),
                missionId,
                1,
                "Inspect workspace",
                DomainTaskStatus.Ready,
                null,
                null,
                now,
                now));

        await store.CreateAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<MissionSnapshot?> RunMissionAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken);

        try
        {
            var snapshot = await store.GetAsync(missionId, cancellationToken);
            if (snapshot is null)
            {
                return null;
            }

            if (snapshot.Mission.Status is MissionStatus.Completed or MissionStatus.Failed)
            {
                return snapshot;
            }

            var startedAt = DateTimeOffset.UtcNow;
            snapshot = snapshot with
            {
                Mission = snapshot.Mission with
                {
                    Status = MissionStatus.Running,
                    Error = null,
                    UpdatedAtUtc = startedAt
                },
                Task = snapshot.Task with
                {
                    Status = DomainTaskStatus.Running,
                    Error = null,
                    UpdatedAtUtc = startedAt
                }
            };

            await store.UpdateAsync(snapshot, cancellationToken);

            try
            {
                var result = await executor.ExecuteAsync(
                    snapshot.Mission,
                    snapshot.Task,
                    cancellationToken);

                var completedAt = DateTimeOffset.UtcNow;
                snapshot = snapshot with
                {
                    Mission = snapshot.Mission with
                    {
                        Status = MissionStatus.Completed,
                        Result = result,
                        Error = null,
                        UpdatedAtUtc = completedAt
                    },
                    Task = snapshot.Task with
                    {
                        Status = DomainTaskStatus.Completed,
                        Result = result,
                        Error = null,
                        UpdatedAtUtc = completedAt
                    }
                };

                await store.UpdateAsync(snapshot, cancellationToken);
                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Keep the persisted Running state. Startup recovery can safely
                // re-run the current deterministic task.
                throw;
            }
            catch (Exception exception)
            {
                var failedAt = DateTimeOffset.UtcNow;
                snapshot = snapshot with
                {
                    Mission = snapshot.Mission with
                    {
                        Status = MissionStatus.Failed,
                        Error = exception.Message,
                        UpdatedAtUtc = failedAt
                    },
                    Task = snapshot.Task with
                    {
                        Status = DomainTaskStatus.Failed,
                        Error = exception.Message,
                        UpdatedAtUtc = failedAt
                    }
                };

                await store.UpdateAsync(snapshot, cancellationToken);
                return snapshot;
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
        var pending = await store.ListRecoverableAsync(cancellationToken);

        foreach (var snapshot in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunMissionAsync(snapshot.Mission.Id, cancellationToken);
        }
    }
}
