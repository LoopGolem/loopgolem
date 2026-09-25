using LoopGolem.Core.Domain;

namespace LoopGolem.Orchestrator;

public interface IMissionStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task CreateAsync(
        MissionSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<MissionSnapshot?> GetAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MissionSnapshot>> ListRecoverableAsync(
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        MissionSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task UpsertTaskAttemptAsync(
        MissionTaskAttempt attempt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MissionTaskAttempt>> ListTaskAttemptsAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task UpsertAgentSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentSession>> ListAgentSessionsAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task UpsertAgentTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTurn>> ListAgentTurnsAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task UpsertRecoveryCycleAsync(
        RecoveryCycle cycle,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecoveryCycle>> ListRecoveryCyclesAsync(
        string missionId,
        CancellationToken cancellationToken = default);
}
