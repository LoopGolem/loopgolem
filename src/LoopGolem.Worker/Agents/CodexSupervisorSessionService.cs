using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed class CodexSupervisorSessionService(
    ICodexSessionTransport transport,
    IMissionStore store)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<CodexStructuredRunResult> RunPlanningAsync(
        Mission mission,
        MissionTask task,
        string model,
        string reasoningEffort,
        string schema,
        string prompt,
        CancellationToken cancellationToken = default) =>
        RunSupervisorTurnAsync(
            mission,
            task.Id,
            AgentTurnPurpose.Planning,
            model,
            reasoningEffort,
            schema,
            prompt,
            cancellationToken);

    public Task<CodexStructuredRunResult> RunRecoveryAsync(
        Mission mission,
        string? taskId,
        string model,
        string reasoningEffort,
        string schema,
        string prompt,
        CancellationToken cancellationToken = default) =>
        RunSupervisorTurnAsync(
            mission,
            taskId,
            AgentTurnPurpose.Recovery,
            model,
            reasoningEffort,
            schema,
            prompt,
            cancellationToken);

    private async Task<CodexStructuredRunResult>
        RunSupervisorTurnAsync(
            Mission mission,
            string? taskId,
            AgentTurnPurpose purpose,
            string model,
            string reasoningEffort,
            string schema,
            string prompt,
            CancellationToken cancellationToken)
    {
        var session =
            await ResolveActiveSupervisorAsync(
                mission.Id,
                model,
                reasoningEffort,
                cancellationToken);

        var mode = session is null
            ? CodexSessionMode.NewPersistent
            : CodexSessionMode.Resume;

        var initialPrompt =
            session is null &&
            purpose == AgentTurnPurpose.Recovery
                ? await BuildResetPromptAsync(
                    mission.Id,
                    prompt,
                    cancellationToken)
                : prompt;

        var request = new CodexStructuredRunRequest(
            mission.Id,
            taskId,
            AgentSessionRole.Supervisor,
            purpose,
            mode,
            session?.Id,
            mission.WorkspacePath,
            model,
            reasoningEffort,
            "read-only",
            schema,
            initialPrompt);

        var result =
            await transport.RunStructuredAsync(
                request,
                cancellationToken);

        if (mode != CodexSessionMode.Resume ||
            session is null ||
            !IsProviderSessionMissing(
                result.Process,
                session.ProviderThreadId!))
        {
            return result;
        }

        await InvalidateAsync(
            session,
            "provider_session_not_found",
            CancellationToken.None);

        var resetPrompt =
            await BuildResetPromptAsync(
                mission.Id,
                prompt,
                cancellationToken);

        return await transport.RunStructuredAsync(
            request with
            {
                SessionMode =
                    CodexSessionMode.NewPersistent,
                SessionId = null,
                Prompt = resetPrompt
            },
            cancellationToken);
    }

    private async Task<AgentSession?>
        ResolveActiveSupervisorAsync(
            string missionId,
            string model,
            string reasoningEffort,
            CancellationToken cancellationToken)
    {
        var sessions =
            await store.ListAgentSessionsAsync(
                missionId,
                cancellationToken);

        var activeSupervisors =
            sessions
                .Where(session =>
                    session.Role ==
                        AgentSessionRole.Supervisor &&
                    session.Status ==
                        AgentSessionStatus.Active)
                .OrderByDescending(
                    session =>
                        session.LastUsedAtUtc)
                .ThenByDescending(
                    session =>
                        session.UpdatedAtUtc)
                .ToArray();

        AgentSession? selected = null;

        foreach (var candidate in activeSupervisors)
        {
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
                    "supervisor_policy_changed",
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

            if (selected is null)
            {
                selected = candidate;
                continue;
            }

            await InvalidateAsync(
                candidate,
                "duplicate_active_supervisor",
                cancellationToken);
        }

        return selected;
    }

    private async Task InvalidateAsync(
        AgentSession session,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAgentSessionAsync(
            session with
            {
                Status =
                    AgentSessionStatus.Invalidated,
                TerminationReason = reason,
                UpdatedAtUtc = now,
                LastUsedAtUtc = now
            },
            cancellationToken);
    }

    private async Task<string> BuildResetPromptAsync(
        string missionId,
        string intendedPrompt,
        CancellationToken cancellationToken)
    {
        var snapshot =
            await store.GetAsync(
                missionId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Mission '{missionId}' disappeared while rebuilding Supervisor context.");

        var capabilities =
            await store.GetCapabilitySnapshotAsync(
                missionId,
                cancellationToken);
        var attempts =
            await store.ListTaskAttemptsAsync(
                missionId,
                cancellationToken);
        var recoveryCycles =
            await store.ListRecoveryCyclesAsync(
                missionId,
                cancellationToken);

        var persistedState =
            JsonSerializer.Serialize(
                new
                {
                    mission = new
                    {
                        snapshot.Mission.Id,
                        snapshot.Mission.Goal,
                        snapshot.Mission.ExecutionMode,
                        snapshot.Mission.Status,
                        snapshot.Mission.Policy
                    },
                    tasks = snapshot.Tasks
                        .OrderBy(task => task.Sequence)
                        .Select(task => new
                        {
                            task.Id,
                            task.Sequence,
                            task.Kind,
                            task.Title,
                            task.Status,
                            task.ExecutionAttemptCount,
                            task.Result,
                            task.ResultDetails,
                            task.Error,
                            task.Definition
                        }),
                    attempts = attempts
                        .OrderBy(attempt =>
                            attempt.StartedAtUtc),
                    recoveryCycles = recoveryCycles
                        .OrderBy(cycle =>
                            cycle.CreatedAtUtc)
                },
                JsonOptions);

        var capabilityContext =
            capabilities is null
                ? "No persisted capability snapshot is available."
                : EnvironmentCapabilityService.FormatForPrompt(
                    capabilities);

        return $"""
            SUPERVISOR SESSION RESET

            The previous persistent Codex Supervisor thread could not be found by the Codex CLI.
            Reconstruct mission context from the persisted LoopGolem state below.
            Treat this persisted state as authoritative for orchestration state.
            Re-inspect repository files when implementation details are needed.
            Do not assume any unfinished prior reasoning survived the reset.

            PERSISTED EXECUTION CAPABILITIES:
            {capabilityContext}

            PERSISTED LOOPGOLEM STATE:
            {persistedState}

            CURRENT TURN:
            {intendedPrompt}
            """;
    }

    internal static bool IsProviderSessionMissing(
        ProcessRunResult process,
        string providerThreadId)
    {
        if (process.TimedOut ||
            process.ExitCode == 0 ||
            string.IsNullOrWhiteSpace(
                providerThreadId))
        {
            return false;
        }

        var expected =
            $"Session not found: {providerThreadId}";

        return process.StandardError.Contains(
                   expected,
                   StringComparison.OrdinalIgnoreCase) ||
               process.StandardOutput.Contains(
                   expected,
                   StringComparison.OrdinalIgnoreCase);
    }
}
