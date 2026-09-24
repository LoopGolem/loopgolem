using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Execution;
using LoopGolem.Worker.Infrastructure;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Worker;

internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"loopgolem-self-test-{Guid.NewGuid():N}");

        var workspace = Path.Combine(root, "workspace");
        var database = Path.Combine(root, "state", "loopgolem.db");

        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "nested"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "alpha.txt"), "alpha");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "nested", "beta.txt"),
                "beta");

            var store = new SqliteMissionStore(database);
            await store.InitializeAsync();

            var orchestrator = new MissionOrchestrator(
                store,
                new WorkspaceInspectionExecutor());

            var created = await orchestrator.CreateMissionAsync(
                "Inspect this workspace.",
                workspace);

            await orchestrator.RunMissionAsync(created.Mission.Id);

            var completed = await store.GetAsync(created.Mission.Id);
            if (completed is null ||
                completed.Mission.Status != MissionStatus.Completed ||
                completed.Task.Status != DomainTaskStatus.Completed ||
                completed.Mission.Result is null ||
                !completed.Mission.Result.Contains("2 files", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("LoopGolem worker self-test failed.");
                return 1;
            }

            Console.WriteLine(completed.Mission.Result);
            Console.WriteLine("LoopGolem worker self-test passed.");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup must not change the test result.
            }
        }
    }
}
