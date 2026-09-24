using LoopGolem.Core.Domain;

namespace LoopGolem.Orchestrator;

public interface IMissionTaskExecutor
{
    MissionTaskKind Kind { get; }

    Task<string> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default);
}
