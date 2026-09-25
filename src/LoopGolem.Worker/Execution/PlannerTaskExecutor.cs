using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;

namespace LoopGolem.Worker.Execution;

public sealed class PlannerTaskExecutor(
    CodexPlanningService planning) : IMissionTaskExecutor
{
    public MissionTaskKind Kind => MissionTaskKind.PlanMission;

    public Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default) =>
        planning.PlanAsync(
            mission,
            task,
            cancellationToken);
}
