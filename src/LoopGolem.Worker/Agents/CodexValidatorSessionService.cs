using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;

namespace LoopGolem.Worker.Agents;

public sealed class CodexValidatorSessionService(
    ICodexSessionTransport transport,
    IMissionStore store)
{
    public async Task<CodexStructuredRunResult>
        RunValidationAsync(
            Mission mission,
            MissionTask task,
            string model,
            string reasoningEffort,
            string schema,
            string prompt,
            CancellationToken cancellationToken = default)
    {
        var session =
            await ResolveActiveValidatorAsync(
                mission.Id,
                model,
                reasoningEffort,
                cancellationToken);

        var mode = session is null
            ? CodexSessionMode.NewPersistent
            : CodexSessionMode.Resume;

        var request = new CodexStructuredRunRequest(
            mission.Id,
            task.Id,
            AgentSessionRole.Validator,
            AgentTurnPurpose.Validation,
            mode,
            session?.Id,
            mission.WorkspacePath,
            model,
            reasoningEffort,
            "read-only",
            schema,
            prompt);

        var result =
            await transport.RunStructuredAsync(
                request,
                cancellationToken);

        if (mode != CodexSessionMode.Resume ||
            session is null ||
            !CodexSupervisorSessionService
                .IsProviderSessionMissing(
                    result.Process,
                    session.ProviderThreadId!))
        {
            return result;
        }

        await InvalidateAsync(
            session,
            "provider_session_not_found",
            CancellationToken.None);

        return await transport.RunStructuredAsync(
            request with
            {
                SessionMode =
                    CodexSessionMode.NewPersistent,
                SessionId = null
            },
            cancellationToken);
    }

    public async Task CompleteValidationAsync(
        string missionId,
        CodexStructuredRunResult run,
        bool accepted,
        bool keepActive,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var sessions =
            await store.ListAgentSessionsAsync(
                missionId,
                cancellationToken);
        var session =
            sessions.FirstOrDefault(
                candidate =>
                    candidate.Id ==
                    run.SessionId &&
                candidate.Role ==
                    AgentSessionRole.Validator);

        if (session is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (session.Status ==
            AgentSessionStatus.Invalidated)
        {
            return;
        }

        var status =
            accepted
                ? keepActive
                    ? AgentSessionStatus.Active
                    : AgentSessionStatus.Closed
                : AgentSessionStatus.Invalidated;

        var terminationReason =
            status == AgentSessionStatus.Active
                ? null
                : reason;

        await store.UpsertAgentSessionAsync(
            session with
            {
                Status = status,
                TerminationReason =
                    terminationReason,
                LastUsedAtUtc = now,
                UpdatedAtUtc = now
            },
            CancellationToken.None);
    }

    private async Task<AgentSession?>
        ResolveActiveValidatorAsync(
            string missionId,
            string model,
            string reasoningEffort,
            CancellationToken cancellationToken)
    {
        var sessions =
            await store.ListAgentSessionsAsync(
                missionId,
                cancellationToken);

        var activeValidators =
            sessions
                .Where(session =>
                    session.Role ==
                        AgentSessionRole.Validator &&
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

        foreach (var candidate in activeValidators)
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
                    "validator_policy_changed",
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
                "duplicate_active_validator",
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
                LastUsedAtUtc = now,
                UpdatedAtUtc = now
            },
            cancellationToken);
    }
}
