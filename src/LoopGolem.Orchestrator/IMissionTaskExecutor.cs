using LoopGolem.Core.Domain;

namespace LoopGolem.Orchestrator;

public interface IMissionTaskExecutor
{
    MissionTaskKind Kind { get; }

    Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default);
}


public interface IMissionTaskExecutionContextProvider
{
    bool RequiresExecutionContext(MissionTask task);

    Task<string> CreateExecutionContextAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default);
}
