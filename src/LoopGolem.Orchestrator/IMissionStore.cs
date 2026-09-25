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

    Task SaveTaskAttemptAsync(
        MissionTaskAttempt attempt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MissionTaskAttempt>> ListTaskAttemptsAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task SaveAgentSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentSession>> ListAgentSessionsAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task SaveAgentTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTurn>> ListAgentTurnsAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task UpdateWithRecoveryEpisodeAsync(
        MissionSnapshot snapshot,
        RecoveryEpisode episode,
        CancellationToken cancellationToken = default);

    Task SaveRecoveryEpisodeAsync(
        RecoveryEpisode episode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecoveryEpisode>> ListRecoveryEpisodesAsync(
        string missionId,
        CancellationToken cancellationToken = default);
}
