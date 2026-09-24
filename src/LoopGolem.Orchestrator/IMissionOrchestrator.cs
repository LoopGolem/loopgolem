using LoopGolem.Core.Domain;

namespace LoopGolem.Orchestrator;

public interface IMissionOrchestrator
{
    Task<MissionSnapshot> CreateMissionAsync(
        string goal,
        string workspacePath,
        MissionExecutionMode executionMode,
        CancellationToken cancellationToken = default);

    Task<MissionSnapshot?> RunMissionAsync(
        string missionId,
        CancellationToken cancellationToken = default);

    Task ResumePendingAsync(
        CancellationToken cancellationToken = default);
}
