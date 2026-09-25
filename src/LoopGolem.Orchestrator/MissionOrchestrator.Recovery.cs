using LoopGolem.Core.Domain;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Orchestrator;

public sealed partial class MissionOrchestrator
{
    private sealed record RecoveryAdvanceResult(
        MissionSnapshot Snapshot,
        bool Terminal,
        bool Reevaluate);

    private bool CanAutomaticallyRecover(
        Mission mission,
        MissionTask task,
        TaskExecutionResult result) =>
        _recoveryPlanner is not null &&
        mission.ExecutionMode ==
            MissionExecutionMode.Codex &&
        mission.Policy.MaxRecoveryCycles > 0 &&
        result.FailureKind ==
            TaskFailureKind.KnownDeterministicFailure &&
        (task.Kind == MissionTaskKind.BuildDotNet ||
         IsDeterministicRunCommand(task));

    private static bool IsDeterministicRunCommand(
        MissionTask task) =>
        task.Kind ==
            MissionTaskKind.DeterministicWork &&
        task.Definition?.Executor ==
            PlannedExecutorKinds.Deterministic &&
        task.Definition.Deterministic.Kind ==
            DeterministicOperationKinds.RunCommand;

    private async Task<int> AllocateAttemptNumberAsync(
        MissionTask task,
        CancellationToken cancellationToken)
    {
        var attempts =
            await _store.ListTaskAttemptsAsync(
                task.MissionId,
                cancellationToken);
        var highestPersisted =
            attempts
                .Where(attempt =>
                    attempt.TaskId == task.Id)
                .Select(attempt =>
                    attempt.AttemptNumber)
                .DefaultIfEmpty(
                    task.ExecutionAttemptCount)
                .Max();

        return checked(
            Math.Max(
                task.ExecutionAttemptCount,
                highestPersisted) + 1);
    }

    private static string BuildAttemptId(
        string taskId,
        int attemptNumber) =>
        $"{taskId}:attempt:{attemptNumber}";

    private async Task<RecoveryAdvanceResult?>
        ReconcilePersistedDeterministicAttemptAsync(
            MissionSnapshot snapshot,
            MissionTask task,
            CancellationToken cancellationToken)
    {
        if (task.Kind is not (
                MissionTaskKind.DeterministicWork or
                MissionTaskKind.BuildDotNet) ||
            task.ExecutionAttemptCount <= 0)
        {
            return null;
        }

        var attempts =
            await _store.ListTaskAttemptsAsync(
                task.MissionId,
                cancellationToken);
        var persisted =
            attempts.FirstOrDefault(
                attempt =>
                    attempt.TaskId == task.Id &&
                    attempt.AttemptNumber ==
                        task.ExecutionAttemptCount);

        if (persisted is null)
        {
            return null;
        }

        var now =
            DateTimeOffset.UtcNow;

        if (persisted.Outcome ==
            MissionTaskAttemptOutcome.Succeeded)
        {
            var completed =
                task with
                {
                    Status =
                        DomainTaskStatus.Completed,
                    Result =
                        persisted.Summary ??
                        "Persisted deterministic attempt succeeded.",
                    ResultDetails =
                        persisted.EvidenceJson,
                    Error = null,
                    UpdatedAtUtc = now
                };

            snapshot =
                ReplaceTask(
                    snapshot,
                    completed);

            await _store.UpdateAsync(
                snapshot,
                cancellationToken);

            await MarkRecoverySucceededIfNeededAsync(
                completed,
                now,
                CancellationToken.None);

            return new RecoveryAdvanceResult(
                snapshot,
                false,
                true);
        }

        if (persisted.Outcome !=
            MissionTaskAttemptOutcome.Failed)
        {
            return null;
        }

        var failure =
            TaskExecutionResult.Failed(
                persisted.Summary ??
                    $"Task '{task.Title}' failed.",
                persisted.Error ??
                    "The persisted deterministic attempt failed.",
                persisted.EvidenceJson,
                failureKind:
                    persisted.FailureKind);

        var failedTask =
            task with
            {
                Status =
                    DomainTaskStatus.Failed,
                Result =
                    failure.Summary,
                ResultDetails =
                    failure.Details,
                Error =
                    failure.Error,
                UpdatedAtUtc = now
            };

        if (CanAutomaticallyRecover(
                snapshot.Mission,
                task,
                failure))
        {
            var recoveryTask =
                failedTask with
                {
                    Status =
                        DomainTaskStatus.RecoveryPending
                };

            snapshot =
                ReplaceTask(
                    snapshot with
                    {
                        Mission =
                            snapshot.Mission with
                            {
                                Status =
                                    MissionStatus.Running,
                                Error = null,
                                UpdatedAtUtc = now
                            }
                    },
                    recoveryTask);

            await _store.UpdateAsync(
                snapshot,
                cancellationToken);

            return await BeginOrContinueRecoveryAsync(
                snapshot,
                recoveryTask,
                persisted,
                now,
                cancellationToken);
        }

        snapshot =
            ReplaceTask(
                snapshot,
                failedTask);

        snapshot =
            await MarkMissionFailedAsync(
                snapshot,
                failedTask.Error ??
                    failure.Summary,
                cancellationToken);

        return new RecoveryAdvanceResult(
            snapshot,
            true,
            false);
    }

    private async Task MarkPreviousAttemptInterruptedAsync(
        MissionTask task,
        CancellationToken cancellationToken)
    {
        if (task.ExecutionAttemptCount <= 0)
        {
            return;
        }

        var attempts =
            await _store.ListTaskAttemptsAsync(
                task.MissionId,
                cancellationToken);
        var prior =
            attempts.FirstOrDefault(
                attempt =>
                    attempt.TaskId == task.Id &&
                    attempt.AttemptNumber ==
                        task.ExecutionAttemptCount);

        if (prior is null ||
            prior.Outcome !=
                MissionTaskAttemptOutcome.Running)
        {
            return;
        }

        await _store.UpsertTaskAttemptAsync(
            prior with
            {
                Outcome =
                    MissionTaskAttemptOutcome.Interrupted,
                Summary =
                    "Task execution was interrupted.",
                Error =
                    "The Worker restarted before the task result was persisted.",
                CompletedAtUtc =
                    DateTimeOffset.UtcNow
            },
            CancellationToken.None);
    }

    private async Task<RecoveryCycle?>
        FindRepairCycleAsync(
            string missionId,
            string taskId,
            CancellationToken cancellationToken)
    {
        var cycles =
            await _store.ListRecoveryCyclesAsync(
                missionId,
                cancellationToken);

        return cycles
            .Where(cycle =>
                cycle.Status ==
                    RecoveryCycleStatus.Repairing &&
                cycle.RepairTaskIds.Contains(
                    taskId,
                    StringComparer.Ordinal))
            .OrderByDescending(
                cycle => cycle.CycleNumber)
            .FirstOrDefault();
    }

    private async Task<RecoveryAdvanceResult>
        BeginOrContinueRecoveryAsync(
            MissionSnapshot snapshot,
            MissionTask recoveryTask,
            MissionTaskAttempt failedAttempt,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var cycles =
            await _store.ListRecoveryCyclesAsync(
                snapshot.Mission.Id,
                cancellationToken);
        var latest =
            cycles
                .Where(cycle =>
                    cycle.FailedTaskId ==
                        recoveryTask.Id)
                .OrderByDescending(
                    cycle =>
                        cycle.CycleNumber)
                .FirstOrDefault();

        var nextCycleNumber = 1;

        if (latest is not null)
        {
            nextCycleNumber =
                checked(
                    latest.CycleNumber + 1);

            if (latest.Status ==
                RecoveryCycleStatus.Retrying)
            {
                if (latest.CycleNumber >=
                    snapshot.Mission.Policy.MaxRecoveryCycles)
                {
                    await _store.UpsertRecoveryCycleAsync(
                        latest with
                        {
                            Status =
                                RecoveryCycleStatus.Exhausted,
                            UpdatedAtUtc = now
                        },
                        CancellationToken.None);

                    var escalated =
                        recoveryTask with
                        {
                            Status =
                                DomainTaskStatus.Escalated,
                            UpdatedAtUtc = now
                        };

                    snapshot =
                        ReplaceTask(
                            snapshot,
                            escalated);

                    snapshot =
                        await MarkMissionNeedsHumanAttentionAsync(
                            snapshot,
                            $"Deterministic check '{recoveryTask.Title}' still failed after {latest.CycleNumber} recovery cycle(s).",
                            cancellationToken);

                    return new RecoveryAdvanceResult(
                        snapshot,
                        true,
                        false);
                }

                await _store.UpsertRecoveryCycleAsync(
                    latest with
                    {
                        Status =
                            RecoveryCycleStatus.Failed,
                        UpdatedAtUtc = now
                    },
                    CancellationToken.None);
            }
            else if (latest.Status is
                RecoveryCycleStatus.Pending or
                RecoveryCycleStatus.Planning or
                RecoveryCycleStatus.Repairing)
            {
                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Recovery state for '{recoveryTask.Title}' is inconsistent: the deterministic check failed while cycle {latest.CycleNumber} was still {latest.Status}.",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }
        }

        if (nextCycleNumber >
            snapshot.Mission.Policy.MaxRecoveryCycles)
        {
            var escalated =
                recoveryTask with
                {
                    Status =
                        DomainTaskStatus.Escalated,
                    UpdatedAtUtc = now
                };

            snapshot =
                ReplaceTask(
                    snapshot,
                    escalated);

            snapshot =
                await MarkMissionNeedsHumanAttentionAsync(
                    snapshot,
                    $"Deterministic check '{recoveryTask.Title}' exhausted its configured recovery limit.",
                    cancellationToken);

            return new RecoveryAdvanceResult(
                snapshot,
                true,
                false);
        }

        var cycle = new RecoveryCycle(
            BuildRecoveryCycleId(
                recoveryTask.Id,
                nextCycleNumber),
            snapshot.Mission.Id,
            recoveryTask.Id,
            nextCycleNumber,
            RecoveryCycleStatus.Pending,
            failedAttempt.Id,
            null,
            [],
            now,
            now);

        await _store.UpsertRecoveryCycleAsync(
            cycle,
            CancellationToken.None);

        return new RecoveryAdvanceResult(
            snapshot,
            false,
            true);
    }

    private async Task<RecoveryAdvanceResult>
        AdvanceRecoveryAsync(
            MissionSnapshot snapshot,
            MissionTask recoveryTask,
            CancellationToken cancellationToken)
    {
        var cycles =
            await _store.ListRecoveryCyclesAsync(
                snapshot.Mission.Id,
                cancellationToken);
        var cycle =
            cycles
                .Where(candidate =>
                    candidate.FailedTaskId ==
                        recoveryTask.Id)
                .OrderByDescending(
                    candidate =>
                        candidate.CycleNumber)
                .FirstOrDefault();

        if (cycle is null)
        {
            var failedAttempt =
                await GetLatestFailedAttemptAsync(
                    recoveryTask,
                    cancellationToken);

            if (failedAttempt is null)
            {
                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Recovery task '{recoveryTask.Title}' has no persisted failed attempt.",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            return await BeginOrContinueRecoveryAsync(
                snapshot,
                recoveryTask,
                failedAttempt,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        if (cycle.Status ==
            RecoveryCycleStatus.Exhausted)
        {
            snapshot =
                await MarkMissionNeedsHumanAttentionAsync(
                    snapshot,
                    $"Recovery for '{recoveryTask.Title}' is exhausted.",
                    cancellationToken);

            return new RecoveryAdvanceResult(
                snapshot,
                true,
                false);
        }

        if (cycle.Status ==
            RecoveryCycleStatus.Failed)
        {
            if (cycle.CycleNumber >=
                snapshot.Mission.Policy.MaxRecoveryCycles)
            {
                await _store.UpsertRecoveryCycleAsync(
                    cycle with
                    {
                        Status =
                            RecoveryCycleStatus.Exhausted,
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);

                var escalated =
                    recoveryTask with
                    {
                        Status =
                            DomainTaskStatus.Escalated,
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    };

                snapshot =
                    ReplaceTask(
                        snapshot,
                        escalated);

                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Recovery for '{recoveryTask.Title}' reached the configured limit of {snapshot.Mission.Policy.MaxRecoveryCycles} cycle(s).",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            var failedAttempt =
                await GetLatestFailedAttemptAsync(
                    recoveryTask,
                    cancellationToken);

            if (failedAttempt is null)
            {
                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Could not find failure evidence for the next recovery cycle of '{recoveryTask.Title}'.",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            var next = new RecoveryCycle(
                BuildRecoveryCycleId(
                    recoveryTask.Id,
                    cycle.CycleNumber + 1),
                snapshot.Mission.Id,
                recoveryTask.Id,
                cycle.CycleNumber + 1,
                RecoveryCycleStatus.Pending,
                failedAttempt.Id,
                null,
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

            await _store.UpsertRecoveryCycleAsync(
                next,
                CancellationToken.None);

            return new RecoveryAdvanceResult(
                snapshot,
                false,
                true);
        }

        if (cycle.Status is
            RecoveryCycleStatus.Pending or
            RecoveryCycleStatus.Planning)
        {
            var persistedRepairs =
                FindPersistedRepairTasks(
                    snapshot,
                    cycle);

            if (persistedRepairs.Count > 0)
            {
                await _store.UpsertRecoveryCycleAsync(
                    cycle with
                    {
                        Status =
                            RecoveryCycleStatus.Repairing,
                        RepairTaskIds =
                            persistedRepairs
                                .Select(task => task.Id)
                                .ToArray(),
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);

                return new RecoveryAdvanceResult(
                    snapshot,
                    false,
                    true);
            }

            if (_recoveryPlanner is null)
            {
                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        "No recovery planner is configured.",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            var attempt =
                await GetAttemptAsync(
                    snapshot.Mission.Id,
                    cycle.FailureAttemptId,
                    cancellationToken);

            if (attempt is null)
            {
                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Recovery cycle {cycle.CycleNumber} has no persisted failure evidence.",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            if (cycle.Status !=
                RecoveryCycleStatus.Planning)
            {
                cycle =
                    cycle with
                    {
                        Status =
                            RecoveryCycleStatus.Planning,
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    };

                await _store.UpsertRecoveryCycleAsync(
                    cycle,
                    CancellationToken.None);
            }

            RecoveryPlanningResult plan;
            try
            {
                plan =
                    await _recoveryPlanner.PlanRecoveryAsync(
                        snapshot.Mission,
                        recoveryTask,
                        attempt,
                        cycle,
                        cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                plan =
                    RecoveryPlanningResult.Failed(
                        "Recovery planner threw an exception.",
                        exception.Message);
            }

            if (!plan.Success)
            {
                await _store.UpsertRecoveryCycleAsync(
                    cycle with
                    {
                        Status =
                            RecoveryCycleStatus.Failed,
                        RecoveryTurnId =
                            plan.RecoveryTurnId,
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);

                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Recovery planning failed for '{recoveryTask.Title}': {plan.Error ?? plan.Summary}",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            var repairError =
                ValidateRecoveryRepairs(
                    snapshot,
                    cycle,
                    plan.Tasks);

            if (repairError is not null)
            {
                await _store.UpsertRecoveryCycleAsync(
                    cycle with
                    {
                        Status =
                            RecoveryCycleStatus.Failed,
                        RecoveryTurnId =
                            plan.RecoveryTurnId,
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);

                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        repairError,
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            var tasks =
                snapshot.Tasks.ToList();
            var sequence =
                tasks.Max(task => task.Sequence) + 1;
            var repairTasks =
                new List<MissionTask>(
                    plan.Tasks.Count);

            foreach (var repair in plan.Tasks)
            {
                var repairTask =
                    NewTask(
                        snapshot.Mission.Id,
                        sequence++,
                        MissionTaskKind.AgentWork,
                        repair.Title,
                        repair,
                        DateTimeOffset.UtcNow);

                tasks.Add(repairTask);
                repairTasks.Add(repairTask);
            }

            snapshot =
                snapshot with
                {
                    Tasks =
                        tasks
                            .OrderBy(task => task.Sequence)
                            .ToArray()
                };

            await _store.UpdateAsync(
                snapshot,
                cancellationToken);

            await _store.UpsertRecoveryCycleAsync(
                cycle with
                {
                    Status =
                        RecoveryCycleStatus.Repairing,
                    RecoveryTurnId =
                        plan.RecoveryTurnId,
                    RepairTaskIds =
                        repairTasks
                            .Select(task => task.Id)
                            .ToArray(),
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                },
                CancellationToken.None);

            return new RecoveryAdvanceResult(
                snapshot,
                false,
                true);
        }

        if (cycle.Status ==
            RecoveryCycleStatus.Repairing)
        {
            var repairTasks =
                ResolveRepairTasks(
                    snapshot,
                    cycle);

            if (repairTasks is null)
            {
                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Recovery cycle {cycle.CycleNumber} references missing repair tasks.",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            var failedRepair =
                repairTasks.FirstOrDefault(
                    task =>
                        task.Status ==
                        DomainTaskStatus.Failed);

            if (failedRepair is not null)
            {
                await _store.UpsertRecoveryCycleAsync(
                    cycle with
                    {
                        Status =
                            RecoveryCycleStatus.Failed,
                        UpdatedAtUtc =
                            DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);

                snapshot =
                    await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        $"Recovery repair task '{failedRepair.Title}' failed.",
                        cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    true,
                    false);
            }

            if (repairTasks.All(
                    task =>
                        task.Status ==
                        DomainTaskStatus.Completed))
            {
                var now =
                    DateTimeOffset.UtcNow;

                await _store.UpsertRecoveryCycleAsync(
                    cycle with
                    {
                        Status =
                            RecoveryCycleStatus.Retrying,
                        UpdatedAtUtc = now
                    },
                    CancellationToken.None);

                snapshot =
                    PrepareRetryAfterRepair(
                        snapshot,
                        recoveryTask,
                        now);

                await _store.UpdateAsync(
                    snapshot,
                    cancellationToken);

                return new RecoveryAdvanceResult(
                    snapshot,
                    false,
                    true);
            }

            return new RecoveryAdvanceResult(
                snapshot,
                false,
                false);
        }

        if (cycle.Status ==
            RecoveryCycleStatus.Retrying)
        {
            var readyForExactRecheck =
                recoveryTask with
                {
                    Status =
                        DomainTaskStatus.Ready,
                    Error = null,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                };

            snapshot =
                ReplaceTask(
                    snapshot,
                    readyForExactRecheck);

            await _store.UpdateAsync(
                snapshot,
                cancellationToken);

            return new RecoveryAdvanceResult(
                snapshot,
                false,
                true);
        }

        snapshot =
            await MarkMissionNeedsHumanAttentionAsync(
                snapshot,
                $"Recovery task '{recoveryTask.Title}' is in unsupported cycle state '{cycle.Status}'.",
                cancellationToken);

        return new RecoveryAdvanceResult(
            snapshot,
            true,
            false);
    }

    private static MissionSnapshot PrepareRetryAfterRepair(
        MissionSnapshot snapshot,
        MissionTask recoveryTask,
        DateTimeOffset now)
    {
        var prerequisiteTaskIds =
            FindRepairReplayPrerequisites(
                snapshot.Tasks,
                recoveryTask);

        if (prerequisiteTaskIds.Count == 0)
        {
            return ReplaceTask(
                snapshot,
                recoveryTask with
                {
                    Status =
                        DomainTaskStatus.Ready,
                    Error = null,
                    UpdatedAtUtc = now
                });
        }

        var tasks =
            snapshot.Tasks
                .Select(task =>
                {
                    if (task.Id ==
                        recoveryTask.Id)
                    {
                        return task with
                        {
                            Status =
                                DomainTaskStatus.Planned,
                            Error = null,
                            UpdatedAtUtc = now
                        };
                    }

                    if (!prerequisiteTaskIds.Contains(
                            task.Id) ||
                        task.Status !=
                            DomainTaskStatus.Completed)
                    {
                        return task;
                    }

                    return task with
                    {
                        Status =
                            DomainTaskStatus.Planned,
                        Result = null,
                        ResultDetails = null,
                        Error = null,
                        UpdatedAtUtc = now
                    };
                })
                .OrderBy(task => task.Sequence)
                .ToArray();

        return UpdateReadyStates(
            snapshot with
            {
                Tasks = tasks
            },
            now);
    }

    private static HashSet<string>
        FindRepairReplayPrerequisites(
            IReadOnlyList<MissionTask> tasks,
            MissionTask failedTask)
    {
        var result =
            new HashSet<string>(
                StringComparer.Ordinal);

        if (failedTask.Definition is null ||
            failedTask.Definition.DependsOn.Count == 0)
        {
            return result;
        }

        var byDefinitionId =
            tasks
                .Where(task =>
                    !string.IsNullOrWhiteSpace(
                        task.Definition?.Id))
                .ToDictionary(
                    task =>
                        task.Definition!.Id,
                    StringComparer.Ordinal);

        var pending =
            new Stack<string>(
                failedTask.Definition.DependsOn);
        var visited =
            new HashSet<string>(
                StringComparer.Ordinal);

        while (pending.Count > 0)
        {
            var dependencyId =
                pending.Pop();

            if (!visited.Add(
                    dependencyId) ||
                !byDefinitionId.TryGetValue(
                    dependencyId,
                    out var dependency))
            {
                continue;
            }

            if (dependency.Status ==
                    DomainTaskStatus.Completed &&
                IsRepairReplayPrerequisite(
                    dependency))
            {
                result.Add(
                    dependency.Id);
            }

            if (dependency.Definition is null)
            {
                continue;
            }

            foreach (var parentId
                     in dependency.Definition.DependsOn)
            {
                pending.Push(
                    parentId);
            }
        }

        return result;
    }

    private static bool IsRepairReplayPrerequisite(
        MissionTask task) =>
        task.Kind ==
            MissionTaskKind.BuildDotNet ||
        task.Kind ==
            MissionTaskKind.DeterministicWork &&
        task.Definition is
        {
            Executor:
                PlannedExecutorKinds.Deterministic,
            RerunAfterRepair: true,
            Deterministic.Kind:
                DeterministicOperationKinds.RunCommand
        };

    private static string? ValidateRecoveryRepairs(
        MissionSnapshot snapshot,
        RecoveryCycle cycle,
        IReadOnlyList<PlannedTask> repairs)
    {
        var validationError =
            MissionPlanValidator.ValidateTasks(
                repairs);

        if (validationError is not null)
        {
            return
                $"Recovery cycle {cycle.CycleNumber} returned invalid repairs: {validationError}";
        }

        if (repairs.Count is < 1 or > 12)
        {
            return
                $"Recovery cycle {cycle.CycleNumber} must contain between 1 and 12 repair tasks.";
        }

        var prefix =
            RecoveryTaskNaming.GetRepairPrefix(
                cycle.FailedTaskId,
                cycle.CycleNumber);
        var existingIds =
            snapshot.Tasks
                .Select(task =>
                    task.Definition?.Id)
                .Where(id =>
                    !string.IsNullOrWhiteSpace(id))
                .ToHashSet(
                    StringComparer.Ordinal);

        foreach (var repair in repairs)
        {
            if (repair.Executor !=
                PlannedExecutorKinds.LunaLow)
            {
                return
                    $"Recovery task '{repair.Id}' must use Luna Low.";
            }

            if (!repair.Id.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
            {
                return
                    $"Recovery task '{repair.Id}' must start with '{prefix}'.";
            }

            if (existingIds.Contains(
                    repair.Id))
            {
                return
                    $"Recovery task id '{repair.Id}' collides with an existing mission task.";
            }
        }

        return null;
    }

    private static IReadOnlyList<MissionTask>
        FindPersistedRepairTasks(
            MissionSnapshot snapshot,
            RecoveryCycle cycle)
    {
        var prefix =
            RecoveryTaskNaming.GetRepairPrefix(
                cycle.FailedTaskId,
                cycle.CycleNumber);

        return snapshot.Tasks
            .Where(task =>
                task.Definition?.Id.StartsWith(
                    prefix,
                    StringComparison.Ordinal) ==
                true)
            .OrderBy(task => task.Sequence)
            .ToArray();
    }

    private static IReadOnlyList<MissionTask>?
        ResolveRepairTasks(
            MissionSnapshot snapshot,
            RecoveryCycle cycle)
    {
        var byId =
            snapshot.Tasks.ToDictionary(
                task => task.Id,
                StringComparer.Ordinal);
        var repairs =
            new List<MissionTask>(
                cycle.RepairTaskIds.Count);

        foreach (var id in cycle.RepairTaskIds)
        {
            if (!byId.TryGetValue(
                    id,
                    out var task))
            {
                return null;
            }

            repairs.Add(task);
        }

        return repairs;
    }

    private async Task<MissionTaskAttempt?>
        GetLatestFailedAttemptAsync(
            MissionTask task,
            CancellationToken cancellationToken)
    {
        var attempts =
            await _store.ListTaskAttemptsAsync(
                task.MissionId,
                cancellationToken);

        return attempts
            .Where(attempt =>
                attempt.TaskId == task.Id &&
                attempt.Outcome ==
                    MissionTaskAttemptOutcome.Failed)
            .OrderByDescending(
                attempt => attempt.AttemptNumber)
            .FirstOrDefault();
    }

    private async Task<MissionTaskAttempt?>
        GetAttemptAsync(
            string missionId,
            string? attemptId,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(
                attemptId))
        {
            return null;
        }

        var attempts =
            await _store.ListTaskAttemptsAsync(
                missionId,
                cancellationToken);

        return attempts.FirstOrDefault(
            attempt =>
                attempt.Id == attemptId);
    }

    private async Task MarkRecoverySucceededIfNeededAsync(
        MissionTask completedTask,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cycles =
            await _store.ListRecoveryCyclesAsync(
                completedTask.MissionId,
                cancellationToken);
        var cycle =
            cycles
                .Where(candidate =>
                    candidate.FailedTaskId ==
                        completedTask.Id &&
                    candidate.Status ==
                        RecoveryCycleStatus.Retrying)
                .OrderByDescending(
                    candidate =>
                        candidate.CycleNumber)
                .FirstOrDefault();

        if (cycle is null)
        {
            return;
        }

        await _store.UpsertRecoveryCycleAsync(
            cycle with
            {
                Status =
                    RecoveryCycleStatus.Succeeded,
                UpdatedAtUtc = now
            },
            cancellationToken);
    }

    private async Task ReconcileCompletedRecoveryCyclesAsync(
        MissionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var cycles =
            await _store.ListRecoveryCyclesAsync(
                snapshot.Mission.Id,
                cancellationToken);

        foreach (var cycle in cycles.Where(
                     candidate =>
                         candidate.Status ==
                         RecoveryCycleStatus.Retrying))
        {
            var task =
                snapshot.Tasks.FirstOrDefault(
                    candidate =>
                        candidate.Id ==
                        cycle.FailedTaskId);

            if (task?.Status !=
                DomainTaskStatus.Completed)
            {
                continue;
            }

            await _store.UpsertRecoveryCycleAsync(
                cycle with
                {
                    Status =
                        RecoveryCycleStatus.Succeeded,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
    }

    private static string BuildRecoveryCycleId(
        string failedTaskId,
        int cycleNumber) =>
        $"{failedTaskId}:recovery:{cycleNumber}";


}
