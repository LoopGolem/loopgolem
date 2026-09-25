using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Execution;
using LoopGolem.Worker.Infrastructure;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Worker;

internal static class SelfTest
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

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
                Console.Error.WriteLine(
                    "Self-test could not initialize Git.");
                return 1;
            }

            if (!await VerifyRunCommandWorkingDirectoriesAsync(
                    processRunner,
                    workspace))
            {
                return 1;
            }

            var baseCommit = await GetHeadAsync(
                processRunner,
                workspace);

            if (baseCommit is null)
            {
                Console.Error.WriteLine(
                    "Self-test could not resolve the baseline commit.");
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
                new FakePlannerExecutor(
                    baseCommit,
                    fakePlan),
                new DeterministicTaskExecutor(processRunner),
                new FakeValidatorExecutor(),
                new GitChangesExecutor(processRunner),
                new DotNetBuildExecutor(processRunner)
            ];

            var orchestrator =
                new MissionOrchestrator(store, executors);

            var created = await orchestrator.CreateMissionAsync(
                "Create generated/message.txt and pass final validation.",
                workspace,
                MissionExecutionMode.Codex);

            if (created.Tasks.Count != 3 ||
                created.Tasks[0].Status != DomainTaskStatus.Ready ||
                created.Tasks[2].Kind != MissionTaskKind.PlanMission)
            {
                Console.Error.WriteLine(
                    "Self-test failed initial planning.");
                return 1;
            }

            var completed = await orchestrator.RunMissionAsync(
                created.Mission.Id);

            if (completed is null ||
                completed.Mission.Status != MissionStatus.Completed ||
                completed.Tasks.Count != 12 ||
                completed.Tasks.Any(
                    task =>
                        task.Status != DomainTaskStatus.Completed))
            {
                Console.Error.WriteLine(
                    "Self-test failed planner/validator mission execution.");
                return 1;
            }

            if (completed.Tasks.Count(
                    task =>
                        task.Kind ==
                        MissionTaskKind.ValidateMission) != 2)
            {
                Console.Error.WriteLine(
                    "Self-test did not execute the validator correction loop.");
                return 1;
            }

            var messagePath = Path.Combine(
                workspace,
                "generated",
                "message.txt");
            var correctionPath = Path.Combine(
                workspace,
                "generated",
                "review.txt");

            if (!File.Exists(messagePath) ||
                await File.ReadAllTextAsync(messagePath) !=
                    "hello from LoopGolem" ||
                !File.Exists(correctionPath) ||
                await File.ReadAllTextAsync(correctionPath) !=
                    "validator correction applied")
            {
                Console.Error.WriteLine(
                    "Self-test deterministic/validator output is invalid.");
                return 1;
            }

            var reopened = new SqliteMissionStore(database);
            await reopened.InitializeAsync();

            var persisted = await reopened.GetAsync(
                created.Mission.Id);

            if (persisted is null ||
                persisted.Tasks.Count != 12 ||
                persisted.Tasks.Count(
                    task =>
                        task.Kind ==
                        MissionTaskKind.ValidateMission) != 2 ||
                persisted.Tasks.Any(
                    task => task.Definition is null))
            {
                Console.Error.WriteLine(
                    "Self-test did not persist expanded validator plan metadata.");
                return 1;
            }

            Console.WriteLine(completed.Mission.Result);
            Console.WriteLine(
                "LoopGolem worker self-test passed.");
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
            new[]
            {
                "config",
                "user.email",
                "loopgolem@example.invalid"
            },
            new[]
            {
                "config",
                "user.name",
                "LoopGolem Self Test"
            },
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

    private static async Task<string?> GetHeadAsync(
        ProcessRunner processRunner,
        string workspace)
    {
        var run = await processRunner.RunAsync(
            "git",
            ["rev-parse", "HEAD"],
            workspace,
            TimeSpan.FromSeconds(30));

        return run.ExitCode == 0 && !run.TimedOut
            ? run.StandardOutput.Trim()
            : null;
    }

    private static async Task<bool>
        VerifyRunCommandWorkingDirectoriesAsync(
            ProcessRunner processRunner,
            string workspace)
    {
        var nestedDirectory = Path.Combine(
            workspace,
            "generated",
            "nested");

        Directory.CreateDirectory(nestedDirectory);

        var executor =
            new DeterministicTaskExecutor(processRunner);

        var executable =
            OperatingSystem.IsWindows()
                ? "cmd.exe"
                : "pwd";

        IReadOnlyList<string> arguments =
            OperatingSystem.IsWindows()
                ? ["/c", "cd"]
                : [];

        foreach (var (
                     workingDirectory,
                     expectedDirectory)
                 in new[]
                 {
                     (".", workspace),
                     (
                         "generated/nested",
                         nestedDirectory)
                 })
        {
            var result = await ExecuteRunCommandAsync(
                executor,
                workspace,
                workingDirectory,
                executable,
                arguments);

            if (!result.Success)
            {
                Console.Error.WriteLine(
                    $"Self-test run_command failed for working directory '{workingDirectory}'.");
                return false;
            }

            var processResult =
                JsonSerializer.Deserialize<ProcessRunResult>(
                    result.Details ?? string.Empty);

            if (processResult is null ||
                !string.Equals(
                    Path.GetFullPath(
                        processResult.StandardOutput.Trim()),
                    Path.GetFullPath(expectedDirectory),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Self-test run_command used the wrong working directory for '{workingDirectory}'.");
                return false;
            }
        }

        var rejected = await ExecuteRunCommandAsync(
            executor,
            workspace,
            "..",
            executable,
            arguments);

        if (rejected.Success)
        {
            Console.Error.WriteLine(
                "Self-test run_command accepted a parent directory.");
            return false;
        }

        return true;
    }

    private static Task<TaskExecutionResult>
        ExecuteRunCommandAsync(
            DeterministicTaskExecutor executor,
            string workspace,
            string workingDirectory,
            string executable,
            IReadOnlyList<string> arguments)
    {
        var operation = new DeterministicOperation(
            DeterministicOperationKinds.RunCommand,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            executable,
            arguments,
            workingDirectory,
            30);

        var definition = new PlannedTask(
            "self-test-run-command",
            "Verify run_command working directory",
            PlannedExecutorKinds.Deterministic,
            string.Empty,
            [],
            [],
            [],
            [],
            operation);

        var mission = new Mission(
            "self-test",
            "Verify run_command working directory handling",
            workspace,
            MissionExecutionMode.Codex,
            MissionStatus.Running,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var task = new MissionTask(
            "self-test-run-command",
            mission.Id,
            0,
            MissionTaskKind.DeterministicWork,
            definition.Title,
            definition,
            DomainTaskStatus.Running,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        return executor.ExecuteAsync(
            mission,
            task);
    }

    private sealed class FakePlannerExecutor(
        string baseCommit,
        MissionPlan plan) : IMissionTaskExecutor
    {
        public MissionTaskKind Kind =>
            MissionTaskKind.PlanMission;

        public Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                TaskExecutionResult.Succeeded(
                    plan.Summary,
                    JsonSerializer.Serialize(
                        new PlannerResult(
                            baseCommit,
                            plan),
                        JsonOptions)));
    }

    private sealed class FakeValidatorExecutor :
        IMissionTaskExecutor
    {
        public MissionTaskKind Kind =>
            MissionTaskKind.ValidateMission;

        public Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default)
        {
            var context =
                JsonSerializer.Deserialize<ValidatorContext>(
                    task.Definition?.Prompt ??
                        string.Empty,
                    JsonOptions);

            if (context is null)
            {
                return Task.FromResult(
                    TaskExecutionResult.Failed(
                        "Fake validator context missing.",
                        "Self-test validator context could not be parsed."));
            }

            ValidationResult result =
                context.Cycle == 1
                    ? new ValidationResult(
                        "not_ok",
                        "Apply one deterministic correction.",
                        [
                            new PlannedTask(
                                "fix1_write-review",
                                "Write validator correction marker",
                                PlannedExecutorKinds.Deterministic,
                                string.Empty,
                                [],
                                ["generated/review.txt"],
                                [],
                                [],
                                new DeterministicOperation(
                                    DeterministicOperationKinds.WriteFile,
                                    "generated/review.txt",
                                    "validator correction applied",
                                    string.Empty,
                                    string.Empty,
                                    string.Empty,
                                    [],
                                    string.Empty,
                                    30))
                        ])
                    : new ValidationResult(
                        "ok",
                        "Final snapshot satisfies the mission.",
                        []);

            return Task.FromResult(
                TaskExecutionResult.Succeeded(
                    result.Summary,
                    JsonSerializer.Serialize(
                        new ValidatorExecutionResult(
                            $"fake-snapshot-{context.Cycle}",
                            result),
                        JsonOptions)));
        }
    }
}
