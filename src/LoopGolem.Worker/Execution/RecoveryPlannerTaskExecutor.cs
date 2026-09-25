using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;

namespace LoopGolem.Worker.Execution;

public sealed class RecoveryPlannerTaskExecutor(
    CodexPlanningService planning) : IMissionTaskExecutor
{
    public MissionTaskKind Kind => MissionTaskKind.PlanRecovery;

    public Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default) =>
        planning.RecoverAsync(
            mission,
            task,
            cancellationToken);
}
