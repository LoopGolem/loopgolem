using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;

namespace LoopGolem.Worker.Execution;

public sealed class MicroTaskAgentExecutor(
    CodexPlanningService planning) : IMissionTaskExecutor
{
    public MissionTaskKind Kind => MissionTaskKind.AgentWork;

    public Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default) =>
        planning.ExecuteMicroTaskAsync(mission, task, cancellationToken);
}
