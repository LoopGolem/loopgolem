using LoopGolem.Orchestrator;
using LoopGolem.Worker;
using LoopGolem.Worker.Agents;
using LoopGolem.Worker.Execution;
using LoopGolem.Worker.Infrastructure;
using LoopGolem.Worker.Ipc;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    return await SelfTest.RunAsync();
}

var store = new SqliteMissionStore(AppPaths.GetDatabasePath());
await store.InitializeAsync();

var processRunner = new ProcessRunner();
var codex = new CodexCliService(processRunner);
var planning = new CodexPlanningService(
    processRunner,
    codex,
    store);

IMissionTaskExecutor[] executors =
[
    new WorkspaceInspectionExecutor(),
    new ProjectDiscoveryExecutor(),
    new CapabilityInspectionExecutor(processRunner, codex),
    new PlannerTaskExecutor(planning),
    new DeterministicTaskExecutor(processRunner),
    new MicroTaskAgentExecutor(planning, processRunner),
    new ValidatorTaskExecutor(planning),
    new GitChangesExecutor(processRunner),
    new DotNetBuildExecutor(processRunner)
];

var orchestrator = new MissionOrchestrator(store, executors);

using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("LoopGolem Worker");
Console.WriteLine($"State: {AppPaths.GetDatabasePath()}");
Console.WriteLine("IPC: local named pipe loopgolem-worker-v1");
Console.WriteLine("Press Ctrl+C to stop.");

var recoveryTask = Task.Run(
    () => orchestrator.ResumePendingAsync(shutdown.Token),
    shutdown.Token);

var server = new WorkerPipeServer(store, orchestrator, codex);

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

try
{
    await recoveryTask;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

return 0;
