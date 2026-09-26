using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Worker.Agents;

public sealed class CodexWorkerSessionService(
    ICodexSessionTransport transport,
    IMissionStore store,
    ICodexSupervisorForkTransport? forkTransport = null)
{
    private const int MinimumAffinityScore = 3;

    public async Task<CodexStructuredRunResult> RunWorkAsync(
        Mission mission,
        MissionTask task,
        string model,
        string reasoningEffort,
        string schema,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        if (mission.Policy.EffectiveWorkerContext ==
            WorkerContextStrategy.SupervisorFork)
        {
            var parentProviderThreadId =
                await ResolveSupervisorProviderThreadAsync(
                    mission,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "SupervisorFork requires an active persistent Supervisor thread.");

            var fork =
                forkTransport ??
                throw new InvalidOperationException(
                    "SupervisorFork transport is unavailable.");

            return await fork.RunForkedWorkerAsync(
                mission,
                task,
                parentProviderThreadId,
                model,
                reasoningEffort,
                schema,
                prompt,
                cancellationToken);
        }

        if (mission.Policy.EffectiveWorkerContext ==
            WorkerContextStrategy.Fresh)
        {
            return await transport.RunStructuredAsync(
                CreateRequest(
                    mission,
                    task,
                    model,
                    reasoningEffort,
                    schema,
                    prompt,
                    CodexSessionMode.FreshEphemeral,
                    null),
                cancellationToken);
        }

        var reusable =
            await ResolveReusableSessionAsync(
                mission,
                task,
                model,
                reasoningEffort,
                cancellationToken);

        var mode = reusable is null
            ? CodexSessionMode.NewPersistent
            : CodexSessionMode.Resume;

        if (reusable is not null)
        {
            reusable =
                await LeaseAsync(
                    reusable,
                    task.Id,
                    cancellationToken);
        }

        var request =
            CreateRequest(
                mission,
                task,
                model,
                reasoningEffort,
                schema,
                prompt,
                mode,
                reusable?.Id);

        try
        {
            var result =
                await transport.RunStructuredAsync(
                    request,
                    cancellationToken);

            if (mode != CodexSessionMode.Resume ||
                reusable is null ||
                !CodexSupervisorSessionService
                    .IsProviderSessionMissing(
                        result.Process,
                        reusable.ProviderThreadId!))
            {
                return result;
            }

            await InvalidateAsync(
                reusable,
                "provider_session_not_found",
                CancellationToken.None);

            return await transport.RunStructuredAsync(
                CreateRequest(
                    mission,
                    task,
                    model,
                    reasoningEffort,
                    schema,
                    prompt,
                    CodexSessionMode.NewPersistent,
                    null),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (reusable is not null)
            {
                await InvalidateAsync(
                    reusable,
                    "worker_resume_exception",
                    CancellationToken.None);
            }

            throw;
        }
    }

    public async Task CompleteWorkAsync(
        Mission mission,
        CodexStructuredRunResult run,
        bool accepted,
        bool contextReuseRecommended,
        string contextReuseReason,
        CancellationToken cancellationToken = default)
    {
        await PersistTurnHintAsync(
            mission.Id,
            run.TurnId,
            contextReuseRecommended,
            contextReuseReason,
            cancellationToken);

        var sessions =
            await store.ListAgentSessionsAsync(
                mission.Id,
                cancellationToken);
        var session =
            sessions.FirstOrDefault(
                candidate =>
                    candidate.Id == run.SessionId);

        if (session is null ||
            session.Role != AgentSessionRole.Worker)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var microtaskCount =
            checked(session.MicrotaskCount + 1);

        var status =
            session.Status;
        string? terminationReason =
            session.TerminationReason;

        if (!accepted)
        {
            status =
                AgentSessionStatus.Invalidated;
            terminationReason =
                "worker_task_rejected";
        }
        else if (mission.Policy.EffectiveWorkerContext ==
                 WorkerContextStrategy.Fresh)
        {
            status =
                AgentSessionStatus.Closed;
            terminationReason ??=
                "ephemeral";
        }
        else if (mission.Policy.EffectiveWorkerContext ==
                 WorkerContextStrategy.SupervisorFork)
        {
            status =
                AgentSessionStatus.Closed;
            terminationReason ??=
                "supervisor_fork_child";
        }
        else if (string.IsNullOrWhiteSpace(
                     session.ProviderThreadId))
        {
            status =
                AgentSessionStatus.Invalidated;
            terminationReason =
                "provider_thread_id_missing";
        }
        else if (!contextReuseRecommended)
        {
            status =
                AgentSessionStatus.Closed;
            terminationReason =
                "worker_context_not_recommended";
        }
        else if (microtaskCount >=
                 Math.Max(
                     1,
                     mission.Policy
                         .MaxWorkerSessionMicrotasks))
        {
            status =
                AgentSessionStatus.Closed;
            terminationReason =
                "worker_microtask_cap";
        }
        else
        {
            status =
                AgentSessionStatus.Active;
            terminationReason = null;
        }

        var updated = session with
        {
            Status = status,
            LeaseOwnerTaskId = null,
            MicrotaskCount = microtaskCount,
            TerminationReason = terminationReason,
            LastUsedAtUtc = now,
            UpdatedAtUtc = now
        };

        await store.UpsertAgentSessionAsync(
            updated,
            CancellationToken.None);

        if (updated.Status ==
            AgentSessionStatus.Active)
        {
            await PruneActiveSessionsAsync(
                mission,
                updated.Id,
                CancellationToken.None);
        }
    }

    private async Task<string?>
        ResolveSupervisorProviderThreadAsync(
            Mission mission,
            CancellationToken cancellationToken)
    {
        var local =
            await ResolveActiveSupervisorThreadAsync(
                mission.Id,
                cancellationToken);

        if (!string.IsNullOrWhiteSpace(local))
        {
            return local;
        }

        var sourceMissionId =
            mission.Policy.SupervisorSourceMissionId;
        if (string.IsNullOrWhiteSpace(
                sourceMissionId))
        {
            return null;
        }

        var source =
            await store.GetAsync(
                sourceMissionId,
                cancellationToken);
        if (source is null ||
            source.Mission.Status !=
                MissionStatus.Paused ||
            !source.Mission.Policy.StopAfterPlanning)
        {
            throw new InvalidOperationException(
                "SupervisorFork source mission must be an existing paused plan-only mission.");
        }

        if (!string.Equals(
                source.Mission.Goal,
                mission.Goal,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFullPath(
                    source.Mission.WorkspacePath),
                Path.GetFullPath(
                    mission.WorkspacePath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SupervisorFork source mission must match the measured mission goal and workspace.");
        }

        return await ResolveActiveSupervisorThreadAsync(
            sourceMissionId,
            cancellationToken);
    }

    private async Task<string?>
        ResolveActiveSupervisorThreadAsync(
            string missionId,
            CancellationToken cancellationToken)
    {
        var sessions =
            await store.ListAgentSessionsAsync(
                missionId,
                cancellationToken);

        return sessions
            .Where(session =>
                session.Role ==
                    AgentSessionRole.Supervisor &&
                session.Status ==
                    AgentSessionStatus.Active &&
                string.Equals(
                    session.ReasoningEffort,
                    "high",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(
                    session.ProviderThreadId))
            .OrderByDescending(
                session =>
                    session.LastUsedAtUtc)
            .Select(session =>
                session.ProviderThreadId)
            .FirstOrDefault();
    }

    private async Task<AgentSession?>
        ResolveReusableSessionAsync(
            Mission mission,
            MissionTask currentTask,
            string model,
            string reasoningEffort,
            CancellationToken cancellationToken)
    {
        var snapshot =
            await store.GetAsync(
                mission.Id,
                cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var sessions =
            await store.ListAgentSessionsAsync(
                mission.Id,
                cancellationToken);
        var turns =
            await store.ListAgentTurnsAsync(
                mission.Id,
                cancellationToken);
        var now =
            DateTimeOffset.UtcNow;
        var maxIdle =
            TimeSpan.FromMinutes(
                Math.Max(
                    1,
                    mission.Policy
                        .MaxWorkerSessionIdleMinutes));
        var maxMicrotasks =
            Math.Max(
                1,
                mission.Policy
                    .MaxWorkerSessionMicrotasks);

        AgentSession? selected = null;
        var selectedScore = int.MinValue;

        foreach (var candidate in sessions
                     .Where(session =>
                         session.Role ==
                             AgentSessionRole.Worker &&
                         session.Status ==
                             AgentSessionStatus.Active)
                     .OrderByDescending(
                         session =>
                             session.LastUsedAtUtc))
        {
            if (!string.IsNullOrWhiteSpace(
                    candidate.LeaseOwnerTaskId))
            {
                await InvalidateAsync(
                    candidate,
                    "stale_worker_lease",
                    cancellationToken);
                continue;
            }

            if (!string.Equals(
                    candidate.Model,
                    model,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    candidate.ReasoningEffort,
                    reasoningEffort,
                    StringComparison.Ordinal))
            {
                await InvalidateAsync(
                    candidate,
                    "worker_policy_changed",
                    cancellationToken);
                continue;
            }

            if (string.IsNullOrWhiteSpace(
                    candidate.ProviderThreadId))
            {
                await InvalidateAsync(
                    candidate,
                    "provider_thread_id_missing",
                    cancellationToken);
                continue;
            }

            if (candidate.MicrotaskCount >=
                maxMicrotasks)
            {
                await CloseAsync(
                    candidate,
                    "worker_microtask_cap",
                    cancellationToken);
                continue;
            }

            if (now - candidate.LastUsedAtUtc >
                maxIdle)
            {
                await CloseAsync(
                    candidate,
                    "worker_session_idle_limit",
                    cancellationToken);
                continue;
            }

            var latestTurn =
                turns
                    .Where(turn =>
                        turn.SessionId ==
                            candidate.Id &&
                        turn.Purpose ==
                            AgentTurnPurpose.Work &&
                        turn.CompletedAtUtc is not null)
                    .OrderByDescending(
                        turn => turn.TurnNumber)
                    .FirstOrDefault();

            if (latestTurn?.ContextReuseRecommended !=
                    true ||
                string.IsNullOrWhiteSpace(
                    latestTurn.TaskId))
            {
                continue;
            }

            var previousTask =
                snapshot.Tasks.FirstOrDefault(
                    task =>
                        task.Id ==
                        latestTurn.TaskId);

            if (previousTask is null ||
                previousTask.Definition is null ||
                currentTask.Definition is null)
            {
                continue;
            }

            if (previousTask.Status !=
                DomainTaskStatus.Completed)
            {
                await InvalidateAsync(
                    candidate,
                    "worker_task_not_committed",
                    cancellationToken);
                continue;
            }

            var score =
                GetAffinityScore(
                    previousTask,
                    currentTask,
                    snapshot.Tasks);

            if (score < MinimumAffinityScore)
            {
                continue;
            }

            if (score > selectedScore ||
                score == selectedScore &&
                selected is not null &&
                candidate.LastUsedAtUtc >
                    selected.LastUsedAtUtc)
            {
                selected = candidate;
                selectedScore = score;
            }
        }

        return selected;
    }

    internal static int GetAffinityScore(
        MissionTask previousTask,
        MissionTask currentTask,
        IReadOnlyList<MissionTask> allTasks)
    {
        var previous =
            previousTask.Definition;
        var current =
            currentTask.Definition;

        if (previous is null ||
            current is null ||
            previousTask.Sequence >=
                currentTask.Sequence)
        {
            return 0;
        }

        var comparer =
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        var previousReads =
            NormalizePaths(
                previous.ReadFiles,
                comparer);
        var previousWrites =
            NormalizePaths(
                GetWritePaths(previous),
                comparer);
        var currentReads =
            NormalizePaths(
                current.ReadFiles,
                comparer);
        var currentWrites =
            NormalizePaths(
                GetWritePaths(current),
                comparer);
        var contextPaths =
            previousReads
                .Concat(previousWrites)
                .Concat(currentReads)
                .Concat(currentWrites)
                .ToHashSet(comparer);

        if (HasConflictingInterveningWrite(
                previousTask,
                currentTask,
                allTasks,
                contextPaths,
                comparer))
        {
            return 0;
        }

        var score = 0;

        if (current.DependsOn.Contains(
                previous.Id,
                StringComparer.Ordinal))
        {
            score += 4;
        }

        if (previousWrites.Overlaps(
                currentReads))
        {
            score += 4;
        }

        if (previousWrites.Overlaps(
                currentWrites))
        {
            score += 2;
        }

        if (previousReads.Overlaps(
                currentReads) ||
            previousReads.Overlaps(
                currentWrites))
        {
            score += 1;
        }

        if (ShareDirectory(
                previousReads
                    .Concat(previousWrites),
                currentReads
                    .Concat(currentWrites),
                comparer))
        {
            score += 1;
        }

        return score;
    }

    private static bool HasConflictingInterveningWrite(
        MissionTask previousTask,
        MissionTask currentTask,
        IReadOnlyList<MissionTask> allTasks,
        HashSet<string> contextPaths,
        StringComparer comparer)
    {
        foreach (var intervening in allTasks)
        {
            if (intervening.Sequence <=
                    previousTask.Sequence ||
                intervening.Sequence >=
                    currentTask.Sequence ||
                intervening.Status !=
                    DomainTaskStatus.Completed ||
                intervening.Definition is null)
            {
                continue;
            }

            var writes =
                NormalizePaths(
                    GetWritePaths(
                        intervening.Definition),
                    comparer);

            if (writes.Overlaps(
                    contextPaths))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> NormalizePaths(
        IEnumerable<string> paths,
        StringComparer comparer) =>
        paths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(comparer);

    private static IEnumerable<string> GetWritePaths(
        PlannedTask task)
    {
        foreach (var path in task.WriteFiles)
        {
            yield return path;
        }

        if (task.Executor !=
            PlannedExecutorKinds.Deterministic)
        {
            yield break;
        }

        if (task.Deterministic.Kind ==
                DeterministicOperationKinds.WriteFile ||
            task.Deterministic.Kind ==
                DeterministicOperationKinds.CreateDirectory)
        {
            yield return task.Deterministic.Path;
        }
        else if (task.Deterministic.Kind ==
                 DeterministicOperationKinds.RenamePath)
        {
            yield return task.Deterministic.SourcePath;
            yield return task.Deterministic.DestinationPath;
        }
    }

    private static string NormalizePath(
        string path)
    {
        var normalized =
            path.Replace(
                '\\',
                '/');

        while (normalized.StartsWith(
                   "./",
                   StringComparison.Ordinal))
        {
            normalized =
                normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    private static bool ShareDirectory(
        IEnumerable<string> first,
        IEnumerable<string> second,
        StringComparer comparer)
    {
        var firstDirectories =
            first
                .Select(GetDirectoryKey)
                .Where(value =>
                    !string.IsNullOrWhiteSpace(
                        value))
                .ToHashSet(comparer);

        return second
            .Select(GetDirectoryKey)
            .Any(directory =>
                !string.IsNullOrWhiteSpace(
                    directory) &&
                firstDirectories.Contains(
                    directory));
    }

    private static string GetDirectoryKey(
        string path)
    {
        var normalized =
            NormalizePath(path);
        var slash =
            normalized.LastIndexOf('/');

        return slash <= 0
            ? string.Empty
            : normalized[..slash];
    }

    private CodexStructuredRunRequest CreateRequest(
        Mission mission,
        MissionTask task,
        string model,
        string reasoningEffort,
        string schema,
        string prompt,
        CodexSessionMode mode,
        string? sessionId) =>
        new(
            mission.Id,
            task.Id,
            AgentSessionRole.Worker,
            AgentTurnPurpose.Work,
            mode,
            sessionId,
            mission.WorkspacePath,
            model,
            reasoningEffort,
            "workspace-write",
            schema,
            prompt)
        {
            LeaseOwnerTaskId = task.Id
        };

    private async Task PersistTurnHintAsync(
        string missionId,
        string turnId,
        bool recommended,
        string reason,
        CancellationToken cancellationToken)
    {
        var turns =
            await store.ListAgentTurnsAsync(
                missionId,
                cancellationToken);
        var turn =
            turns.FirstOrDefault(
                candidate =>
                    candidate.Id == turnId);

        if (turn is null)
        {
            return;
        }

        await store.UpsertAgentTurnAsync(
            turn with
            {
                ContextReuseRecommended =
                    recommended,
                ContextReuseReason =
                    reason
            },
            CancellationToken.None);
    }

    private async Task<AgentSession> LeaseAsync(
        AgentSession session,
        string taskId,
        CancellationToken cancellationToken)
    {
        var now =
            DateTimeOffset.UtcNow;
        var leased =
            session with
            {
                LeaseOwnerTaskId = taskId,
                UpdatedAtUtc = now
            };

        await store.UpsertAgentSessionAsync(
            leased,
            cancellationToken);

        return leased;
    }

    private async Task PruneActiveSessionsAsync(
        Mission mission,
        string keepSessionId,
        CancellationToken cancellationToken)
    {
        var limit =
            Math.Max(
                1,
                mission.Policy
                    .MaxActiveWorkerSessions);
        var sessions =
            await store.ListAgentSessionsAsync(
                mission.Id,
                cancellationToken);
        var active =
            sessions
                .Where(session =>
                    session.Role ==
                        AgentSessionRole.Worker &&
                    session.Status ==
                        AgentSessionStatus.Active &&
                    string.IsNullOrWhiteSpace(
                        session.LeaseOwnerTaskId))
                .OrderByDescending(
                    session =>
                        session.LastUsedAtUtc)
                .ToArray();

        if (active.Length <= limit)
        {
            return;
        }

        var keep =
            active.FirstOrDefault(
                session =>
                    session.Id ==
                    keepSessionId);
        var retained =
            active
                .Where(session =>
                    session.Id !=
                        keepSessionId)
                .Take(
                    Math.Max(
                        0,
                        limit - (keep is null ? 0 : 1)))
                .Select(session =>
                    session.Id)
                .ToHashSet(
                    StringComparer.Ordinal);

        if (keep is not null)
        {
            retained.Add(
                keep.Id);
        }

        foreach (var session in active)
        {
            if (retained.Contains(
                    session.Id))
            {
                continue;
            }

            await CloseAsync(
                session,
                "worker_active_session_cap",
                cancellationToken);
        }
    }

    private async Task CloseAsync(
        AgentSession session,
        string reason,
        CancellationToken cancellationToken)
    {
        var now =
            DateTimeOffset.UtcNow;

        await store.UpsertAgentSessionAsync(
            session with
            {
                Status =
                    AgentSessionStatus.Closed,
                LeaseOwnerTaskId = null,
                TerminationReason = reason,
                LastUsedAtUtc = now,
                UpdatedAtUtc = now
            },
            cancellationToken);
    }

    private async Task InvalidateAsync(
        AgentSession session,
        string reason,
        CancellationToken cancellationToken)
    {
        var now =
            DateTimeOffset.UtcNow;

        await store.UpsertAgentSessionAsync(
            session with
            {
                Status =
                    AgentSessionStatus.Invalidated,
                LeaseOwnerTaskId = null,
                TerminationReason = reason,
                LastUsedAtUtc = now,
                UpdatedAtUtc = now
            },
            cancellationToken);
    }
}
