using LoopGolem.Core.Domain;

namespace LoopGolem.Orchestrator;

public interface IMissionTaskExecutor
{
    Task<string> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default);
}
