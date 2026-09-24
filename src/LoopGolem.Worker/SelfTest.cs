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
            Directory.CreateDirectory(workspace);
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

            var processRunner = new ProcessRunner();
            if (!await InitializeGitAsync(processRunner, workspace))
            {
                Console.Error.WriteLine("Self-test could not initialize Git.");
                return 1;
            }

            var store = new SqliteMissionStore(database);
            await store.InitializeAsync();

            var fakePlan = new MissionPlan(
                "Create generated content with dependent deterministic tasks.",
                [
                    new PlannedTask(
                        "make-dir",
                        "Create generated directory",
                        PlannedExecutorKinds.Deterministic,
                        string.Empty,
                        [],
                        ["generated"],
                        [],
                        [],
                        new DeterministicOperation(
                            DeterministicOperationKinds.CreateDirectory,
                            "generated",
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            [],
                            string.Empty,
                            30)),
                    new PlannedTask(
                        "write-message",
                        "Write generated message",
                        PlannedExecutorKinds.Deterministic,
                        string.Empty,
                        [],
                        ["generated/message.txt"],
                        [],
                        ["make-dir"],
                        new DeterministicOperation(
                            DeterministicOperationKinds.WriteFile,
                            "generated/message.txt",
                            "hello from LoopGolem",
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            [],
                            string.Empty,
                            30))
                ],
                ["dotnet build Demo.csproj"]);

            IMissionTaskExecutor[] executors =
            [
                new WorkspaceInspectionExecutor(),
                new ProjectDiscoveryExecutor(),
                new FakePlannerExecutor(fakePlan),
                new DeterministicTaskExecutor(processRunner),
                new GitChangesExecutor(processRunner),
                new DotNetBuildExecutor(processRunner)
            ];

            var orchestrator = new MissionOrchestrator(store, executors);
            var created = await orchestrator.CreateMissionAsync(
                "Create generated/message.txt.",
                workspace,
                MissionExecutionMode.Codex);

            if (created.Tasks.Count != 3 ||
                created.Tasks[0].Status != DomainTaskStatus.Ready ||
                created.Tasks[2].Kind != MissionTaskKind.PlanMission)
            {
                Console.Error.WriteLine("Self-test failed initial planning.");
                return 1;
            }

            var completed = await orchestrator.RunMissionAsync(created.Mission.Id);

            if (completed is null ||
                completed.Mission.Status != MissionStatus.Completed ||
                completed.Tasks.Count != 7 ||
                completed.Tasks.Any(task => task.Status != DomainTaskStatus.Completed))
            {
                Console.Error.WriteLine("Self-test failed dynamic execution.");
                return 1;
            }

            var messagePath = Path.Combine(workspace, "generated", "message.txt");
            if (!File.Exists(messagePath) ||
                await File.ReadAllTextAsync(messagePath) != "hello from LoopGolem")
            {
                Console.Error.WriteLine("Self-test deterministic output is invalid.");
                return 1;
            }

            var reopened = new SqliteMissionStore(database);
            await reopened.InitializeAsync();
            var persisted = await reopened.GetAsync(created.Mission.Id);

            if (persisted is null ||
                persisted.Tasks.Count != 7 ||
                persisted.Tasks.Count(task =>
                    task.Kind == MissionTaskKind.DeterministicWork) != 2 ||
                persisted.Tasks.Any(task => task.Definition is null))
            {
                Console.Error.WriteLine("Self-test did not persist plan metadata.");
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
            }
        }
    }

    private static async Task<bool> InitializeGitAsync(
        ProcessRunner processRunner,
        string workspace)
    {
        var commands = new[]
        {
            new[] { "init" },
            new[] { "config", "user.email", "loopgolem@example.invalid" },
            new[] { "config", "user.name", "LoopGolem Self Test" },
            new[] { "add", "." },
            new[] { "commit", "-m", "baseline" }
        };

        foreach (var arguments in commands)
        {
            var run = await processRunner.RunAsync(
                "git",
                arguments,
                workspace,
                TimeSpan.FromSeconds(30));

            if (run.ExitCode != 0 || run.TimedOut)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class FakePlannerExecutor(
        MissionPlan plan) : IMissionTaskExecutor
    {
        public MissionTaskKind Kind => MissionTaskKind.PlanMission;

        public Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                TaskExecutionResult.Succeeded(
                    plan.Summary,
                    JsonSerializer.Serialize(plan)));
    }
}
