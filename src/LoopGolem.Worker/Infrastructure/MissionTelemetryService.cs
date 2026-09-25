using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Orchestrator;

namespace LoopGolem.Worker.Infrastructure;

public sealed class MissionTelemetryService(
    IMissionStore store)
{
    public async Task<MissionTelemetrySummary> GetSummaryAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        var sessions =
            await store.ListAgentSessionsAsync(
                missionId,
                cancellationToken);
        var turns =
            await store.ListAgentTurnsAsync(
                missionId,
                cancellationToken);
        var recoveryCycles =
            await store.ListRecoveryCyclesAsync(
                missionId,
                cancellationToken);

        var roleSummaries =
            Enum.GetValues<AgentSessionRole>()
                .Select(role =>
                {
                    var roleSessions =
                        sessions.Where(
                            session =>
                                session.Role == role)
                            .ToArray();
                    var sessionIds =
                        roleSessions
                            .Select(session => session.Id)
                            .ToHashSet(
                                StringComparer.Ordinal);
                    var roleTurns =
                        turns.Where(
                            turn =>
                                sessionIds.Contains(
                                    turn.SessionId))
                            .ToArray();

                    return new AgentRoleTelemetrySummary(
                        role,
                        roleSessions.Length,
                        roleTurns.Length,
                        roleTurns.Sum(
                            turn => turn.InputTokens),
                        roleTurns.Sum(
                            turn => turn.CachedInputTokens),
                        roleTurns.Sum(
                            turn => turn.CacheWriteInputTokens),
                        roleTurns.Sum(
                            turn => turn.OutputTokens),
                        roleTurns.Sum(
                            turn => turn.ReasoningOutputTokens),
                        roleTurns.Sum(
                            turn => turn.TotalTokens));
                })
                .ToArray();

        return new MissionTelemetrySummary(
            sessions.Count,
            sessions.Count(
                session =>
                    session.Status ==
                        AgentSessionStatus.Active),
            sessions.Count(
                session =>
                    session.Status ==
                        AgentSessionStatus.Invalidated),
            turns.Count,
            recoveryCycles.Count,
            recoveryCycles.Count(
                cycle =>
                    cycle.Status ==
                        RecoveryCycleStatus.Succeeded),
            recoveryCycles.Count(
                cycle =>
                    cycle.Status ==
                        RecoveryCycleStatus.Exhausted),
            turns.Count(
                turn =>
                    turn.Purpose ==
                        AgentTurnPurpose.Work &&
                    turn.TurnNumber > 1),
            turns.Count(
                turn =>
                    turn.Purpose ==
                        AgentTurnPurpose.Work &&
                    turn.ContextReuseRecommended ==
                        true),
            turns.Sum(
                turn => turn.InputTokens),
            turns.Sum(
                turn => turn.CachedInputTokens),
            turns.Sum(
                turn => turn.CacheWriteInputTokens),
            turns.Sum(
                turn => turn.OutputTokens),
            turns.Sum(
                turn => turn.ReasoningOutputTokens),
            turns.Sum(
                turn => turn.TotalTokens),
            roleSummaries);
    }
}
