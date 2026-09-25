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
var codexTransport =
    new CodexSessionTransport(
        processRunner,
        codex,
        store);
var capabilityService =
    new EnvironmentCapabilityService(
        processRunner,
        store);
var supervisor =
    new CodexSupervisorSessionService(
        codexTransport,
        store);
var workerSessions =
    new CodexWorkerSessionService(
        codexTransport,
        store);
var planning =
    new CodexPlanningService(
        processRunner,
        codex,
        codexTransport,
        supervisor,
        workerSessions,
        capabilityService);

IMissionTaskExecutor[] executors =
[
    new WorkspaceInspectionExecutor(),
    new ProjectDiscoveryExecutor(),
    new PlannerTaskExecutor(planning),
    new DeterministicTaskExecutor(processRunner),
    new MicroTaskAgentExecutor(planning, processRunner),
    new ValidatorTaskExecutor(planning),
    new GitChangesExecutor(processRunner),
    new DotNetBuildExecutor(processRunner)
];

var orchestrator =
    new MissionOrchestrator(
        store,
        executors,
        planning);

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
