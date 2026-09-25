using System.Text.Json;
using LoopGolem.Core.Domain;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Orchestrator;

public sealed partial class MissionOrchestrator : IMissionOrchestrator
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
                    var reconciled =
                        await ReconcilePersistedDeterministicAttemptAsync(
                            snapshot,
                            current,
                            cancellationToken);

                    if (reconciled is not null)
                    {
                        snapshot =
                            reconciled.Snapshot;

                        if (reconciled.Terminal)
                        {
                            return snapshot;
                        }

                        if (reconciled.Reevaluate)
                        {
                            continue;
                        }
                    }

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
                var nextAttemptNumber =
                    await AllocateAttemptNumberAsync(
                        current,
                        cancellationToken);
                var running = current with
                {
                    Status = recovering
                        ? DomainTaskStatus.Retrying
                        : DomainTaskStatus.Running,
                    ExecutionContext = executionContext,
                    ExecutionAttemptCount =
                        nextAttemptNumber,
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
                        CompletedAtUtc = finishedAt,
                        FailureKind = result.FailureKind
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
        }ber}";

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
