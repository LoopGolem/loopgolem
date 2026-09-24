using System.Text.Json;
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
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Class1.cs"),
                "public sealed class Class1 { }");

            var store = new SqliteMissionStore(database);
            await store.InitializeAsync();

            var processRunner = new ProcessRunner();

            IMissionTaskExecutor[] executors =
            [
                new WorkspaceInspectionExecutor(),
                new ProjectDiscoveryExecutor(),
                new DotNetBuildExecutor(processRunner)
            ];

            var orchestrator = new MissionOrchestrator(store, executors);

            var created = await orchestrator.CreateMissionAsync(
                "Inspect and build this workspace.",
                workspace);

            if (created.Tasks.Count != 3 ||
                created.Tasks[0].Status != DomainTaskStatus.Ready ||
                created.Tasks[1].Status != DomainTaskStatus.Planned ||
                created.Tasks[2].Status != DomainTaskStatus.Planned)
            {
                Console.Error.WriteLine(
                    "LoopGolem worker self-test failed while planning tasks.");
                return 1;
            }

            await orchestrator.RunMissionAsync(created.Mission.Id);

            var reopenedStore = new SqliteMissionStore(database);
            await reopenedStore.InitializeAsync();
            var completed = await reopenedStore.GetAsync(created.Mission.Id);

            if (completed is null ||
                completed.Mission.Status != MissionStatus.Completed ||
                completed.Tasks.Count != 3 ||
                completed.Tasks.Any(task => task.Status != DomainTaskStatus.Completed) ||
                completed.Tasks.Any(task => string.IsNullOrWhiteSpace(task.Result)))
            {
                Console.Error.WriteLine("LoopGolem worker self-test failed.");
                return 1;
            }

            var buildTask = completed.Tasks.Single(
                task => task.Kind == MissionTaskKind.BuildDotNet);

            if (string.IsNullOrWhiteSpace(buildTask.ResultDetails))
            {
                Console.Error.WriteLine(
                    "LoopGolem worker self-test did not persist process details.");
                return 1;
            }

            var buildRun = JsonSerializer.Deserialize<ProcessRunResult>(
                buildTask.ResultDetails);

            if (buildRun is null ||
                buildRun.ExitCode != 0 ||
                buildRun.TimedOut)
            {
                Console.Error.WriteLine(
                    "LoopGolem worker self-test build process failed.");
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
