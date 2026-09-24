using LoopGolem.Orchestrator;
using LoopGolem.Worker;
using LoopGolem.Worker.Execution;
using LoopGolem.Worker.Infrastructure;
using LoopGolem.Worker.Ipc;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    return await SelfTest.RunAsync();
}

var store = new SqliteMissionStore(AppPaths.GetDatabasePath());
await store.InitializeAsync();

IMissionTaskExecutor[] executors =
[
    new WorkspaceInspectionExecutor(),
    new ProjectDiscoveryExecutor()
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

var server = new WorkerPipeServer(store, orchestrator);

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Normal shutdown.
}

try
{
    await recoveryTask;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Normal shutdown.
}

return 0;
