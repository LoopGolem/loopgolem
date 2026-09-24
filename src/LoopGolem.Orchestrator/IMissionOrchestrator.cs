namespace LoopGolem.Orchestrator;

public interface IMissionOrchestrator
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
