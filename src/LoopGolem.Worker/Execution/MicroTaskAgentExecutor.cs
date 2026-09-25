using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Execution;

public sealed class MicroTaskAgentExecutor(
    CodexPlanningService planning,
    ProcessRunner processRunner) :
    IMissionTaskExecutor,
    IMissionTaskExecutionContextProvider
{
    public MissionTaskKind Kind => MissionTaskKind.AgentWork;

    public bool RequiresExecutionContext(MissionTask task) => true;

    public async Task<string> CreateExecutionContextAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GitWorkspaceSnapshot.CaptureAsync(
            processRunner,
            mission.WorkspacePath,
            cancellationToken);

        return JsonSerializer.Serialize(snapshot);
    }

    public Task<TaskExecutionResult> ExecuteAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default) =>
        planning.ExecuteMicroTaskAsync(mission, task, cancellationToken);
}
