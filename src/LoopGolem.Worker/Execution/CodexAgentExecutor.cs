using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;

namespace LoopGolem.Worker.Execution;

public sealed class CodexAgentExecutor(CodexCliService codex)
    : IMissionTaskExecutor
{
    public MissionTaskKind Kind => MissionTaskKind.AgentWork;

    public Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default) =>
        codex.ExecuteAsync(mission, task, cancellationToken);
}
