using System.Text.Json;
using LoopGolem.Core.Domain;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Orchestrator;

public sealed class MissionOrchestrator : IMissionOrchestrator
{
    private const int MaxValidationCycles = 3;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IMissionStore _store;
    private readonly IReadOnlyDictionary<MissionTaskKind, IMissionTaskExecutor> _executors;
    private readonly IMissionRecoveryPlanner? _recoveryPlanner;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public MissionOrchestrator(
        IMissionStore store,
        IEnumerable<IMissionTaskExecutor> executors,
        IMissionRecoveryPlanner? recoveryPlanner = null)
    {
        _store = store;
        _recoveryPlanner = recoveryPlanner;
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

            if (snapshot.Mission.Status is
                MissionStatus.Completed or
                MissionStatus.Failed or
                MissionStatus.NeedsHumanAttention)
            {
                return snapshot;
            }

            await ReconcileCompletedRecoveryCyclesAsync(
                snapshot,
                cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = UpdateReadyStates(
                    snapshot,
                    DateTimeOffset.UtcNow);

                var incomplete = snapshot.Tasks
                    .Where(task =>
                        task.Status != DomainTaskStatus.Completed)
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

                    await _store.UpdateAsync(
                        snapshot,
                        cancellationToken);

                    return snapshot;
                }

                var recoveryPending =
                    incomplete.FirstOrDefault(
                        task =>
                            task.Status ==
                            DomainTaskStatus.RecoveryPending);

                if (recoveryPending is not null)
                {
                    var recoveryAdvance =
                        await AdvanceRecoveryAsync(
                            snapshot,
                            recoveryPending,
                            cancellationToken);

                    snapshot =
                        recoveryAdvance.Snapshot;

                    if (recoveryAdvance.Terminal)
                    {
                        return snapshot;
                    }

                    if (recoveryAdvance.Reevaluate)
                    {
                        continue;
                    }
                }

                var failed = incomplete.FirstOrDefault(
                    task =>
                        task.Status == DomainTaskStatus.Failed);

                if (failed is not null)
                {
                    return await MarkMissionFailedAsync(
                        snapshot,
                        failed.Error ??
                            $"Task '{failed.Title}' failed.",
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

                if (!_executors.TryGetValue(
                        current.Kind,
                        out var executor))
                {
                    return await MarkMissionFailedAsync(
                        snapshot,
                        $"No executor is registered for task kind '{current.Kind}'.",
                        cancellationToken);
                }

                var recovering = current.Status is
                    DomainTaskStatus.Running or
                    DomainTaskStatus.Retrying;

                if (recovering)
                {
                    await MarkPreviousAttemptInterruptedAsync(
                        current,
                        cancellationToken);
                }

                if (recovering &&
                    IsUnsafeInterruptedRunCommand(current))
                {
                    return await MarkMissionNeedsHumanAttentionAsync(
                        snapshot,
                        "An interrupted deterministic run_command cannot be automatically replayed because its side effects may already have occurred.",
                        cancellationToken);
                }

                string? executionContext =
                    current.ExecutionContext;

                if (executor is
                        IMissionTaskExecutionContextProvider contextProvider &&
                    contextProvider.RequiresExecutionContext(current) &&
                    string.IsNullOrWhiteSpace(executionContext))
                {
                    if (recovering)
                    {
                        return await MarkMissionNeedsHumanAttentionAsync(
                            snapshot,
                            $"Task '{current.Title}' was interrupted before LoopGolem had a persisted execution baseline.",
                            cancellationToken);
                    }

                    try
                    {
                        executionContext =
                            await contextProvider.CreateExecutionContextAsync(
                                snapshot.Mission,
                                current,
                                cancellationToken);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        return await MarkMissionFailedAsync(
                            snapshot,
                            $"Could not prepare task '{current.Title}' for crash-safe execution: {exception.Message}",
                            cancellationToken);
                    }
                }

                var startedAt = DateTimeOffset.UtcNow;
                var running = current with
                {
                    Status = recovering
                        ? DomainTaskStatus.Retrying
                        : DomainTaskStatus.Running,
                    ExecutionContext = executionContext,
                    ExecutionAttemptCount = checked(
                        current.ExecutionAttemptCount + 1),
                    Error = null,
                    UpdatedAtUtc = startedAt
                };

                snapshot = ReplaceTask(
                    snapshot with
                    {
                        Mission = snapshot.Mission with
                        {
                            Status =
                                current.Kind == MissionTaskKind.PlanMission
                                    ? MissionStatus.Planning
                                    : MissionStatus.Running,
                            Error = null,
                            UpdatedAtUtc = startedAt
                        }
                    },
                    running);

                await _store.UpdateAsync(
                    snapshot,
                    cancellationToken);

                var attempt = new MissionTaskAttempt(
                    BuildAttemptId(
                        running.Id,
                        running.ExecutionAttemptCount),
                    running.MissionId,
                    running.Id,
                    running.ExecutionAttemptCount,
                    MissionTaskAttemptOutcome.Running,
                    null,
                    null,
                    null,
                    startedAt,
                    null);

                await _store.UpsertTaskAttemptAsync(
                    attempt,
                    cancellationToken);

                TaskExecutionResult result;
                try
                {
                    result = await executor.ExecuteAsync(
                        snapshot.Mission,
                        running,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    await _store.UpsertTaskAttemptAsync(
                        attempt with
                        {
                            Outcome =
                                MissionTaskAttemptOutcome.Interrupted,
                            Summary =
                                "Task execution was interrupted.",
                            Error =
                                "The Worker stopped before the task result was persisted.",
                            CompletedAtUtc =
                                DateTimeOffset.UtcNow
                        },
                        CancellationToken.None);

                    throw;
                }
                catch (Exception exception)
                {
                    result = TaskExecutionResult.Failed(
                        $"Task '{running.Title}' threw an exception.",
                        exception.Message);
                }

                var finishedAt = DateTimeOffset.UtcNow;

                if (!result.Success)
                {
                    var failedAttempt = attempt with
                    {
                        Outcome =
                            MissionTaskAttemptOutcome.Failed,
                        Summary = result.Summary,
                        Error =
                            result.Error ??
                            result.Summary,
                        EvidenceJson = result.Details,
                        CompletedAtUtc = finishedAt
                    };

                    await _store.UpsertTaskAttemptAsync(
                        failedAttempt,
                        CancellationToken.None);

                    var failedTask = running with
                    {
                        Status = DomainTaskStatus.Failed,
                        Result = result.Summary,
                        ResultDetails = result.Details,
                        TokenUsage = CombineTokenUsage(
                            running.TokenUsage,
                            result.TokenUsage),
                        Error =
                            result.Error ??
                            result.Summary,
                        UpdatedAtUtc = finishedAt
                    };

                    var repairCycle =
                        await FindRepairCycleAsync(
                            snapshot.Mission.Id,
                            running.Id,
                            cancellationToken);

                    if (repairCycle is not null)
                    {
                        snapshot = ReplaceTask(
                            snapshot,
                            failedTask);

                        snapshot = snapshot with
                        {
                            Mission = snapshot.Mission with
                            {
                                Status =
                                    MissionStatus.NeedsHumanAttention,
                                Result =
                                    $"Recovery repair task '{running.Title}' failed.",
                                Error = null,
                                UpdatedAtUtc = finishedAt
                            }
                        };

                        await _store.UpdateAsync(
                            snapshot,
                            cancellationToken);

                        await _store.UpsertRecoveryCycleAsync(
                            repairCycle with
                            {
                                Status =
                                    RecoveryCycleStatus.Failed,
                                UpdatedAtUtc = finishedAt
                            },
                            CancellationToken.None);

                        return snapshot;
                    }

                    if (CanAutomaticallyRecover(
                            snapshot.Mission,
                            running,
                            result))
                    {
                        var recoveryTask = failedTask with
                        {
                            Status =
                                DomainTaskStatus.RecoveryPending
                        };

                        snapshot = ReplaceTask(
                            snapshot with
                            {
                                Mission =
                                    snapshot.Mission with
                                    {
                                        Status =
                                            MissionStatus.Running,
                                        Error = null,
                                        UpdatedAtUtc = finishedAt
                                    }
                            },
                            recoveryTask);

                        await _store.UpdateAsync(
                            snapshot,
                            cancellationToken);

                        var recoveryFailure =
                            await BeginOrContinueRecoveryAsync(
                                snapshot,
                                recoveryTask,
                                failedAttempt,
                                finishedAt,
                                cancellationToken);

                        snapshot =
                            recoveryFailure.Snapshot;

                        if (recoveryFailure.Terminal)
                        {
                            return snapshot;
                        }

                        continue;
                    }

                    snapshot = ReplaceTask(
                        snapshot,
                        failedTask);

                    snapshot = snapshot with
                    {
                        Mission = snapshot.Mission with
                        {
                            Status = MissionStatus.Failed,
                            Error = failedTask.Error,
                            UpdatedAtUtc = finishedAt
                        }
                    };

                    await _store.UpdateAsync(
                        snapshot,
                        cancellationToken);

                    return snapshot;
                }

                var completed = running with
                {
                    Status = DomainTaskStatus.Completed,
                    Result = result.Summary,
                    ResultDetails = result.Details,
                    TokenUsage = CombineTokenUsage(
                        running.TokenUsage,
                        result.TokenUsage),
                    Error = null,
                    UpdatedAtUtc = finishedAt
                };

                await _store.UpsertTaskAttemptAsync(
                    attempt with
                    {
                        Outcome =
                            MissionTaskAttemptOutcome.Succeeded,
                        Summary = result.Summary,
                        Error = null,
                        EvidenceJson = result.Details,
                        CompletedAtUtc = finishedAt
                    },
                    CancellationToken.None);

                snapshot = ReplaceTask(
                    snapshot,
                    completed);

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
                else if (
                    completed.Kind == MissionTaskKind.ValidateMission)
                {
                    var expansion = ExpandValidationResult(
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

                    if (expansion.NeedsHumanAttention)
                    {
                        snapshot = snapshot with
                        {
                            Mission = snapshot.Mission with
                            {
                                Status =
                                    MissionStatus.NeedsHumanAttention,
                                Result = completed.Result,
                                Error = null,
                                UpdatedAtUtc = finishedAt
                            }
                        };

                        await _store.UpdateAsync(
                            snapshot,
                            cancellationToken);

                        return snapshot;
                    }
                }

                snapshot = UpdateReadyStates(
                    snapshot,
                    finishedAt);

                snapshot = snapshot with
                {
                    Mission = snapshot.Mission with
                    {
                        Status = MissionStatus.Running,
                        UpdatedAtUtc = finishedAt
                    }
                };

                await _store.UpdateAsync(
                    snapshot,
                    cancellationToken);

                await MarkRecoverySucceededIfNeededAsync(
                    completed,
                    finishedAt,
                    CancellationToken.None);
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
        var pending = await _store.ListRecoverableAsync(
            cancellationToken);

        foreach (var snapshot in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await RunMissionAsync(
                snapshot.Mission.Id,
                cancellationToken);
        }
    }

    private static IReadOnlyList<MissionTask> CreateInitialPlan(
        string missionId,
        MissionExecutionMode executionMode,
        DateTimeOffset now)
    {
        var steps =
            new List<(
                MissionTaskKind Kind,
                string Title,
                PlannedTask Definition)>
            {
                (
                    MissionTaskKind.InspectWorkspace,
                    "Inspect workspace",
                    InternalDefinition(
                        "inspect-workspace",
                        [])),
                (
                    MissionTaskKind.DiscoverProjects,
                    "Discover project files",
                    InternalDefinition(
                        "discover-projects",
                        ["inspect-workspace"]))
            };

        steps.Add(
            executionMode == MissionExecutionMode.Codex
                ? (
                    MissionTaskKind.PlanMission,
                    "Plan mission",
                    InternalDefinition(
                        "plan-mission",
                        ["discover-projects"]))
                : (
                    MissionTaskKind.BuildDotNet,
                    "Build .NET workspace",
                    InternalDefinition(
                        "build-dotnet",
                        ["discover-projects"])));

        return steps
            .Select(
                (step, index) =>
                    NewTask(
                        missionId,
                        index + 1,
                        step.Kind,
                        step.Title,
                        step.Definition,
                        now))
            .ToArray();
    }

    private static (
        MissionSnapshot? Snapshot,
        string? Error) ExpandPlannerResult(
        MissionSnapshot snapshot,
        MissionTask plannerTask,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(
                plannerTask.ResultDetails))
        {
            return (
                null,
                "Planner completed without a structured mission plan.");
        }

        PlannerResult? result;
        try
        {
            result = JsonSerializer.Deserialize<PlannerResult>(
                plannerTask.ResultDetails,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return (
                null,
                $"Planner returned invalid JSON: {exception.Message}");
        }

        if (result is null ||
            string.IsNullOrWhiteSpace(result.BaseCommit))
        {
            return (
                null,
                "Planner returned an incomplete plan context.");
        }

        var planError =
            MissionPlanValidator.Validate(result.Plan);

        if (planError is not null)
        {
            return (null, planError);
        }

        var tasks = snapshot.Tasks.ToList();
        var sequence =
            tasks.Max(task => task.Sequence) + 1;

        foreach (var definition in result.Plan.Tasks)
        {
            tasks.Add(
                NewTask(
                    snapshot.Mission.Id,
                    sequence++,
                    GetKind(definition),
                    definition.Title,
                    definition,
                    now));
        }

        var dependencies =
            result.Plan.Tasks.Count == 0
                ? new[] { "plan-mission" }
                : result.Plan.Tasks
                    .Select(task => task.Id)
                    .ToArray();

        AppendVerificationAndValidator(
            tasks,
            snapshot.Mission.Id,
            ref sequence,
            dependencies,
            result.BaseCommit,
            result.Plan,
            [],
            cycle: 1,
            now);

        return (
            snapshot with
            {
                Tasks = tasks
                    .OrderBy(task => task.Sequence)
                    .ToArray()
            },
            null);
    }

    private static (
        MissionSnapshot? Snapshot,
        string? Error,
        bool NeedsHumanAttention)
        ExpandValidationResult(
            MissionSnapshot snapshot,
            MissionTask validatorTask,
            DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(
                validatorTask.ResultDetails))
        {
            return (
                null,
                "Validator completed without a structured result.",
                false);
        }

        ValidatorExecutionResult? execution;
        try
        {
            execution =
                JsonSerializer.Deserialize<ValidatorExecutionResult>(
                    validatorTask.ResultDetails,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            return (
                null,
                $"Validator returned invalid JSON: {exception.Message}",
                false);
        }

        if (execution is null)
        {
            return (
                null,
                "Validator returned an empty result.",
                false);
        }

        var context = ParseValidatorContext(
            validatorTask);

        if (context.Error is not null)
        {
            return (
                null,
                context.Error,
                false);
        }

        var validatorContext = context.Context!;

        if (execution.Result.Status == "ok")
        {
            return (
                snapshot,
                null,
                false);
        }

        if (execution.Result.Status != "not_ok")
        {
            return (
                null,
                $"Validator returned unsupported status '{execution.Result.Status}'.",
                false);
        }

        if (validatorContext.Cycle >=
            MaxValidationCycles)
        {
            return (
                snapshot,
                null,
                true);
        }

        var corrections =
            execution.Result.Tasks;

        var correctionError =
            MissionPlanValidator.ValidateTasks(
                corrections);

        if (correctionError is not null)
        {
            return (
                null,
                $"Validator correction plan is invalid: {correctionError}",
                false);
        }

        if (corrections.Count == 0)
        {
            return (
                null,
                "Validator returned not_ok without correction tasks.",
                false);
        }

        var existingIds = snapshot.Tasks
            .Select(task => task.Definition?.Id)
            .Where(id =>
                !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        if (corrections.Any(
                correction =>
                    existingIds.Contains(correction.Id)))
        {
            return (
                null,
                "Validator correction task ids collide with existing mission task ids.",
                false);
        }

        var correctionIds = corrections
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var correction in corrections)
        {
            if (correction.DependsOn.Any(
                    dependency =>
                        !correctionIds.Contains(dependency)))
            {
                return (
                    null,
                    $"Correction task '{correction.Id}' may depend only on tasks from the same correction batch.",
                    false);
            }
        }

        var tasks = snapshot.Tasks.ToList();
        var sequence =
            tasks.Max(task => task.Sequence) + 1;

        foreach (var correction in corrections)
        {
            tasks.Add(
                NewTask(
                    snapshot.Mission.Id,
                    sequence++,
                    GetKind(correction),
                    correction.Title,
                    correction,
                    now));
        }

        var dependencies = corrections
            .Select(task => task.Id)
            .ToArray();

        AppendVerificationAndValidator(
            tasks,
            snapshot.Mission.Id,
            ref sequence,
            dependencies,
            validatorContext.BaseCommit,
            validatorContext.Plan,
            validatorContext.CorrectionHistory
                .Concat(corrections)
                .ToArray(),
            validatorContext.Cycle + 1,
            now);

        return (
            snapshot with
            {
                Tasks = tasks
                    .OrderBy(task => task.Sequence)
                    .ToArray()
            },
            null,
            false);
    }

    private static void AppendVerificationAndValidator(
        List<MissionTask> tasks,
        string missionId,
        ref int sequence,
        IReadOnlyList<string> dependencies,
        string baseCommit,
        MissionPlan plan,
        IReadOnlyList<PlannedTask> correctionHistory,
        int cycle,
        DateTimeOffset now)
    {
        var inspectId = $"inspect-git-{cycle}";
        var buildId = $"build-dotnet-{cycle}";
        var validatorId = $"validate-{cycle}";

        tasks.Add(
            NewTask(
                missionId,
                sequence++,
                MissionTaskKind.InspectGitChanges,
                "Inspect Git changes",
                InternalDefinition(
                    inspectId,
                    dependencies),
                now));

        tasks.Add(
            NewTask(
                missionId,
                sequence++,
                MissionTaskKind.BuildDotNet,
                "Build .NET workspace",
                InternalDefinition(
                    buildId,
                    [inspectId]),
                now));

        tasks.Add(
            NewTask(
                missionId,
                sequence++,
                MissionTaskKind.ValidateMission,
                "Validate mission",
                ValidatorDefinition(
                    validatorId,
                    [buildId],
                    baseCommit,
                    plan,
                    correctionHistory,
                    cycle),
                now));
    }

    private static (
        ValidatorContext? Context,
        string? Error) ParseValidatorContext(
        MissionTask validatorTask)
    {
        if (validatorTask.Definition is null ||
            string.IsNullOrWhiteSpace(
                validatorTask.Definition.Prompt))
        {
            return (
                null,
                "Validator task context is missing.");
        }

        try
        {
            var context =
                JsonSerializer.Deserialize<ValidatorContext>(
                    validatorTask.Definition.Prompt,
                    JsonOptions);

            return context is null
                ? (
                    null,
                    "Validator task context is empty.")
                : (context, null);
        }
        catch (JsonException exception)
        {
            return (
                null,
                $"Validator task context is invalid: {exception.Message}");
        }
    }

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

    private static string BuildAttemptId(
        string taskId,
        int attemptNumber) =>
        $"{taskId}:attempt:{attemptNumber}";

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

                var readyForExactRecheck =
                    recoveryTask with
                    {
                        Status =
                            DomainTaskStatus.Ready,
                        Error = null,
                        UpdatedAtUtc = now
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
            $"repair{cycle.CycleNumber}_";
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
            $"repair{cycle.CycleNumber}_";

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

    private static MissionSnapshot UpdateReadyStates(
        MissionSnapshot snapshot,
        DateTimeOffset now)
    {
        var tasks = snapshot.Tasks.ToArray();
        var changed = false;

        for (var index = 0;
             index < tasks.Length;
             index++)
        {
            var task = tasks[index];

            if (task.Status !=
                    DomainTaskStatus.Planned ||
                !DependenciesSatisfied(
                    task,
                    tasks))
            {
                continue;
            }

            tasks[index] = task with
            {
                Status = DomainTaskStatus.Ready,
                UpdatedAtUtc = now
            };

            changed = true;
        }

        return changed
            ? snapshot with { Tasks = tasks }
            : snapshot;
    }

    private static bool DependenciesSatisfied(
        MissionTask task,
        IReadOnlyList<MissionTask> allTasks)
    {
        if (task.Definition is null)
        {
            return allTasks
                .Where(other =>
                    other.Sequence < task.Sequence)
                .All(other =>
                    other.Status ==
                    DomainTaskStatus.Completed);
        }

        foreach (var dependencyId
                 in task.Definition.DependsOn)
        {
            var dependency =
                allTasks.FirstOrDefault(
                    candidate =>
                        string.Equals(
                            candidate.Definition?.Id,
                            dependencyId,
                            StringComparison.Ordinal));

            if (dependency is null ||
                dependency.Status !=
                    DomainTaskStatus.Completed)
            {
                return false;
            }
        }

        return true;
    }

    private static TokenUsage? CombineTokenUsage(
        TokenUsage? accumulated,
        TokenUsage? current) =>
        accumulated is null
            ? current
            : current is null
                ? accumulated
                : accumulated.Add(current);

    private async Task<MissionSnapshot>
        MarkMissionNeedsHumanAttentionAsync(
            MissionSnapshot snapshot,
            string reason,
            CancellationToken cancellationToken)
    {
        snapshot = snapshot with
        {
            Mission = snapshot.Mission with
            {
                Status = MissionStatus.NeedsHumanAttention,
                Result = reason,
                Error = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await _store.UpdateAsync(
            snapshot,
            cancellationToken);

        return snapshot;
    }

    private static bool IsUnsafeInterruptedRunCommand(
        MissionTask task) =>
        task.Kind == MissionTaskKind.DeterministicWork &&
        task.Definition?.Executor ==
            PlannedExecutorKinds.Deterministic &&
        task.Definition.Deterministic.Kind ==
            DeterministicOperationKinds.RunCommand;

    private async Task<MissionSnapshot>
        MarkMissionFailedAsync(
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
                UpdatedAtUtc =
                    DateTimeOffset.UtcNow
            }
        };

        await _store.UpdateAsync(
            snapshot,
            cancellationToken);

        return snapshot;
    }

    private static MissionTaskKind GetKind(
        PlannedTask definition) =>
        definition.Executor ==
        PlannedExecutorKinds.Deterministic
            ? MissionTaskKind.DeterministicWork
            : MissionTaskKind.AgentWork;

    private static MissionTask NewTask(
        string missionId,
        int sequence,
        MissionTaskKind kind,
        string title,
        PlannedTask definition,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid().ToString("N"),
            missionId,
            sequence,
            kind,
            title,
            definition,
            DomainTaskStatus.Planned,
            null,
            null,
            null,
            now,
            now);

    private static MissionSnapshot ReplaceTask(
        MissionSnapshot snapshot,
        MissionTask replacement) =>
        snapshot with
        {
            Tasks = snapshot.Tasks
                .Select(task =>
                    task.Id == replacement.Id
                        ? replacement
                        : task)
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
            EmptyOperation());

    private static PlannedTask ValidatorDefinition(
        string id,
        IReadOnlyList<string> dependsOn,
        string baseCommit,
        MissionPlan plan,
        IReadOnlyList<PlannedTask> correctionHistory,
        int cycle) =>
        new(
            id,
            "Validate mission",
            PlannedExecutorKinds.Internal,
            JsonSerializer.Serialize(
                new ValidatorContext(
                    baseCommit,
                    plan,
                    correctionHistory,
                    cycle),
                JsonOptions),
            [],
            [],
            [],
            dependsOn,
            EmptyOperation());

    private static DeterministicOperation EmptyOperation() =>
        new(
            DeterministicOperationKinds.None,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            string.Empty,
            60);

    private static string BuildMissionResult(
        IEnumerable<MissionTask> tasks) =>
        string.Join(
            Environment.NewLine,
            tasks
                .OrderBy(task => task.Sequence)
                .Where(task =>
                    !string.IsNullOrWhiteSpace(
                        task.Result))
                .Select(task =>
                    $"{task.Title}: {task.Result}"));
}
