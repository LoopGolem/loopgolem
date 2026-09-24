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
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Demo.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var store = new SqliteMissionStore(database);
            await store.InitializeAsync();

            IMissionTaskExecutor[] executors =
            [
                new WorkspaceInspectionExecutor(),
                new ProjectDiscoveryExecutor()
            ];

            var orchestrator = new MissionOrchestrator(store, executors);

            var created = await orchestrator.CreateMissionAsync(
                "Inspect this workspace.",
                workspace);

            if (created.Tasks.Count != 2 ||
                created.Tasks[0].Status != DomainTaskStatus.Ready ||
                created.Tasks[1].Status != DomainTaskStatus.Planned)
            {
                Console.Error.WriteLine(
                    "LoopGolem worker self-test failed while planning tasks.");
                return 1;
            }

            await orchestrator.RunMissionAsync(created.Mission.Id);

            // Re-open the store to prove the result came from durable state,
            // not an object still held in memory.
            var reopenedStore = new SqliteMissionStore(database);
            await reopenedStore.InitializeAsync();
            var completed = await reopenedStore.GetAsync(created.Mission.Id);

            if (completed is null ||
                completed.Mission.Status != MissionStatus.Completed ||
                completed.Tasks.Count != 2 ||
                completed.Tasks.Any(task => task.Status != DomainTaskStatus.Completed) ||
                completed.Mission.Result is null ||
                !completed.Tasks[0].Result!.Contains("3 files", StringComparison.Ordinal) ||
                !completed.Tasks[1].Result!.Contains("Demo.csproj", StringComparison.Ordinal))
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
