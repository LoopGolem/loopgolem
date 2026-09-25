using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;

namespace LoopGolem.Worker.Execution;

public sealed class ValidatorTaskExecutor(
    CodexPlanningService planning) : IMissionTaskExecutor
{
    public MissionTaskKind Kind => MissionTaskKind.ValidateMission;

    public Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default) =>
        planning.ValidateAsync(
            mission,
            task,
            cancellationToken);
}
