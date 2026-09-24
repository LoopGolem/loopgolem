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
}
