using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;
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

            if (!await VerifyInterruptedRenameRecoveryAsync(
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

            if (!await VerifyPreparedTaskRecoveryAsync(
                    processRunner,
                    workspace,
                    baseCommit,
                    root))
            {
                return 1;
            }

            if (!await VerifyInterruptedRunCommandStopsAsync(
                    processRunner,
                    workspace,
                    root))
            {
                return 1;
            }

            if (!await VerifyGitSnapshotAsync(
                    processRunner,
                    workspace,
                    baseCommit))
            {
                return 1;
            }

            if (!await VerifyGitChangeCountingAsync(
                    processRunner,
                    workspace))
            {
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

            if (!VerifySelfHostingPrompts(
                    workspace,
                    baseCommit,
                    fakePlan))
            {
                return 1;
            }

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
                        task.Status != DomainTaskStatus.Completed ||
                        task.ExecutionAttemptCount != 1))
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

    private static async Task<bool> VerifyGitChangeCountingAsync(
        ProcessRunner processRunner,
        string workspace)
    {
        var firstPath = Path.Combine(
            workspace,
            "git-count-first.txt");
        var secondPath = Path.Combine(
            workspace,
            "git-count-second.txt");

        await File.WriteAllTextAsync(
            firstPath,
            "first");
        await File.WriteAllTextAsync(
            secondPath,
            "second");

        try
        {
            var now = DateTimeOffset.UtcNow;
            var mission = new Mission(
                "git-counting",
                "Verify changed-path counting.",
                workspace,
                MissionExecutionMode.Codex,
                MissionStatus.Running,
                null,
                null,
                now,
                now);
            var task = new MissionTask(
                "git-counting",
                mission.Id,
                1,
                MissionTaskKind.InspectGitChanges,
                "Inspect Git changes",
                null,
                DomainTaskStatus.Running,
                null,
                null,
                null,
                now,
                now);

            var result =
                await new GitChangesExecutor(
                    processRunner).ExecuteAsync(
                    mission,
                    task);

            if (!result.Success ||
                result.Summary !=
                    "Git reports 2 changed path(s).")
            {
                Console.Error.WriteLine(
                    $"Self-test Git changed-path count is invalid: {result.Summary}");
                return false;
            }

            return true;
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    private static async Task<bool> VerifyGitSnapshotAsync(
        ProcessRunner processRunner,
        string workspace,
        string baseCommit)
    {
        var probePath = Path.Combine(
            workspace,
            "snapshot-probe.txt");

        await File.WriteAllTextAsync(
            probePath,
            "snapshot only");

        try
        {
            var snapshots =
                new GitSnapshotService(processRunner);

            var snapshotCommit =
                await snapshots.CreateSnapshotCommitAsync(
                    workspace,
                    baseCommit,
                    "self-test");

            var diff = await processRunner.RunAsync(
                "git",
                [
                    "diff",
                    "--name-only",
                    $"{baseCommit}..{snapshotCommit}",
                    "--"
                ],
                workspace,
                TimeSpan.FromSeconds(30));

            if (diff.ExitCode != 0 ||
                !diff.StandardOutput
                    .Split(
                        ['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
                    .Contains(
                        "snapshot-probe.txt",
                        StringComparer.Ordinal))
            {
                Console.Error.WriteLine(
                    "Self-test snapshot commit did not contain the working-tree probe.");
                return false;
            }

            var currentHead = await GetHeadAsync(
                processRunner,
                workspace);

            if (!string.Equals(
                    currentHead,
                    baseCommit,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "Self-test snapshot moved the user's HEAD.");
                return false;
            }

            var status = await processRunner.RunAsync(
                "git",
                ["status", "--short"],
                workspace,
                TimeSpan.FromSeconds(30));

            if (status.ExitCode != 0 ||
                !status.StandardOutput.Contains(
                    "snapshot-probe.txt",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "Self-test snapshot unexpectedly changed the real Git index/worktree state.");
                return false;
            }

            return true;
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private static async Task<bool>
        VerifyInterruptedRenameRecoveryAsync(
            ProcessRunner processRunner,
            string workspace)
    {
        var sourcePath = Path.Combine(
            workspace,
            "rename-source.txt");
        var destinationPath = Path.Combine(
            workspace,
            "rename-destination.txt");

        await File.WriteAllTextAsync(
            sourcePath,
            "rename recovery");

        var executor =
            new DeterministicTaskExecutor(processRunner);
        var definition = new PlannedTask(
            "rename-recovery",
            "Verify interrupted rename recovery",
            PlannedExecutorKinds.Deterministic,
            string.Empty,
            [],
            ["rename-source.txt", "rename-destination.txt"],
            [],
            [],
            new DeterministicOperation(
                DeterministicOperationKinds.RenamePath,
                string.Empty,
                string.Empty,
                "rename-source.txt",
                "rename-destination.txt",
                string.Empty,
                [],
                string.Empty,
                30));
        var now = DateTimeOffset.UtcNow;
        var mission = new Mission(
            "rename-recovery",
            "Verify interrupted rename recovery",
            workspace,
            MissionExecutionMode.Codex,
            MissionStatus.Running,
            null,
            null,
            now,
            now);
        var task = new MissionTask(
            "rename-recovery",
            mission.Id,
            1,
            MissionTaskKind.DeterministicWork,
            definition.Title,
            definition,
            DomainTaskStatus.Running,
            null,
            null,
            null,
            now,
            now);

        try
        {
            var context =
                await executor.CreateExecutionContextAsync(
                    mission,
                    task);

            File.Move(
                sourcePath,
                destinationPath);

            var recovered = task with
            {
                Status = DomainTaskStatus.Retrying,
                ExecutionContext = context
            };

            var result = await executor.ExecuteAsync(
                mission,
                recovered);

            if (!result.Success ||
                !File.Exists(destinationPath) ||
                File.Exists(sourcePath))
            {
                Console.Error.WriteLine(
                    "Self-test failed interrupted rename recovery.");
                return false;
            }

            return true;
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    private static async Task<bool>
        VerifyPreparedTaskRecoveryAsync(
            ProcessRunner processRunner,
            string workspace,
            string baseCommit,
            string root)
    {
        var database = Path.Combine(
            root,
            "state",
            "recovery-context.db");
        var store = new SqliteMissionStore(database);
        await store.InitializeAsync();

        var plan = new MissionPlan(
            "Exercise persisted execution context recovery.",
            [
                new PlannedTask(
                    "recover-agent",
                    "Recover prepared agent task",
                    PlannedExecutorKinds.LunaLow,
                    "Verify crash-safe recovery.",
                    [],
                    [],
                    [],
                    [],
                    new DeterministicOperation(
                        DeterministicOperationKinds.None,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        [],
                        string.Empty,
                        30))
            ],
            []);

        using var interrupted = new CancellationTokenSource();

        IMissionTaskExecutor[] firstExecutors =
        [
            new WorkspaceInspectionExecutor(),
            new ProjectDiscoveryExecutor(),
            new FakePlannerExecutor(
                baseCommit,
                plan),
            new InterruptingPreparedExecutor(interrupted)
        ];

        var firstOrchestrator =
            new MissionOrchestrator(
                store,
                firstExecutors);

        var created =
            await firstOrchestrator.CreateMissionAsync(
                "Exercise persisted execution context recovery.",
                workspace,
                MissionExecutionMode.Codex);

        try
        {
            await firstOrchestrator.RunMissionAsync(
                created.Mission.Id,
                interrupted.Token);

            Console.Error.WriteLine(
                "Self-test expected the prepared task to be interrupted.");
            return false;
        }
        catch (OperationCanceledException)
            when (interrupted.IsCancellationRequested)
        {
        }

        var reopened = new SqliteMissionStore(database);
        await reopened.InitializeAsync();

        var interruptedSnapshot =
            await reopened.GetAsync(
                created.Mission.Id);
        var interruptedTask =
            interruptedSnapshot?.Tasks.FirstOrDefault(
                task =>
                    task.Kind ==
                    MissionTaskKind.AgentWork);

        if (interruptedSnapshot is null ||
            interruptedTask is null ||
            interruptedTask.Status !=
                DomainTaskStatus.Running ||
            interruptedTask.ExecutionContext !=
                PreparedRecoveryContext ||
            interruptedTask.ExecutionAttemptCount != 1)
        {
            Console.Error.WriteLine(
                "Self-test did not persist the pre-execution recovery context.");
            return false;
        }

        IMissionTaskExecutor[] recoveryExecutors =
        [
            new RecoveringPreparedExecutor(),
            new GitChangesExecutor(processRunner),
            new DotNetBuildExecutor(processRunner),
            new AlwaysOkValidatorExecutor()
        ];

        var recoveryOrchestrator =
            new MissionOrchestrator(
                reopened,
                recoveryExecutors);

        var completed =
            await recoveryOrchestrator.RunMissionAsync(
                created.Mission.Id);

        if (completed is null ||
            completed.Mission.Status !=
                MissionStatus.Completed ||
            completed.Tasks.First(
                task =>
                    task.Kind ==
                    MissionTaskKind.AgentWork).Status !=
                DomainTaskStatus.Completed ||
            completed.Tasks.First(
                task =>
                    task.Kind ==
                    MissionTaskKind.AgentWork).ExecutionAttemptCount != 2)
        {
            Console.Error.WriteLine(
                "Self-test did not resume the interrupted prepared task.");
            return false;
        }

        var recoveredStore = new SqliteMissionStore(database);
        await recoveredStore.InitializeAsync();

        var recoveredSnapshot = await recoveredStore.GetAsync(
            created.Mission.Id);
        var recoveredTask = recoveredSnapshot?.Tasks.FirstOrDefault(
            task =>
                task.Kind ==
                MissionTaskKind.AgentWork);

        if (recoveredTask is null ||
            recoveredTask.Status != DomainTaskStatus.Completed ||
            recoveredTask.ExecutionAttemptCount != 2)
        {
            Console.Error.WriteLine(
                "Self-test did not persist the recovered task attempt count.");
            return false;
        }

        return true;
    }

    private static async Task<bool>
        VerifyInterruptedRunCommandStopsAsync(
            ProcessRunner processRunner,
            string workspace,
            string root)
    {
        var database = Path.Combine(
            root,
            "state",
            "unsafe-command.db");
        var store = new SqliteMissionStore(database);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var missionId =
            Guid.NewGuid().ToString("N");
        var definition = new PlannedTask(
            "unsafe-command",
            "Do not replay interrupted command",
            PlannedExecutorKinds.Deterministic,
            string.Empty,
            [],
            [],
            [],
            [],
            new DeterministicOperation(
                DeterministicOperationKinds.RunCommand,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "loopgolem-command-must-not-run",
                [],
                ".",
                30));
        var snapshot = new MissionSnapshot(
            new Mission(
                missionId,
                "Do not replay an interrupted command.",
                workspace,
                MissionExecutionMode.Codex,
                MissionStatus.Running,
                null,
                null,
                now,
                now),
            [
                new MissionTask(
                    Guid.NewGuid().ToString("N"),
                    missionId,
                    1,
                    MissionTaskKind.DeterministicWork,
                    definition.Title,
                    definition,
                    DomainTaskStatus.Running,
                    null,
                    null,
                    null,
                    now,
                    now)
            ]);

        await store.CreateAsync(snapshot);

        var orchestrator =
            new MissionOrchestrator(
                store,
                [
                    new DeterministicTaskExecutor(
                        processRunner)
                ]);

        var recovered =
            await orchestrator.RunMissionAsync(
                missionId);

        if (recovered is null ||
            recovered.Mission.Status !=
                MissionStatus.NeedsHumanAttention)
        {
            Console.Error.WriteLine(
                "Self-test replayed or failed an interrupted run_command instead of requesting human attention.");
            return false;
        }

        return true;
    }

    private const string PreparedRecoveryContext =
        "self-test-persisted-baseline";

    private static bool VerifySelfHostingPrompts(
        string workspace,
        string baseCommit,
        MissionPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = new Mission(
            "self-hosting-prompt-test",
            "Modify LoopGolem.Worker safely.",
            workspace,
            MissionExecutionMode.Codex,
            MissionStatus.Running,
            null,
            null,
            now,
            now);

        var plannerPrompt =
            CodexPlanningService.BuildPlannerPrompt(mission);
        var validatorPrompt =
            CodexPlanningService.BuildValidatorPrompt(
                mission,
                new ValidatorContext(
                    baseCommit,
                    plan,
                    [],
                    1),
                "self-test-snapshot");

        if (!plannerPrompt.Contains(
                CodexPlanningService.SelfHostingRule,
                StringComparison.Ordinal) ||
            !validatorPrompt.Contains(
                CodexPlanningService.SelfHostingRule,
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Self-test self-hosting policy is missing from a Codex prompt.");
            return false;
        }

        return true;
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

    private sealed class InterruptingPreparedExecutor(
        CancellationTokenSource interruption) :
        IMissionTaskExecutor,
        IMissionTaskExecutionContextProvider
    {
        public MissionTaskKind Kind =>
            MissionTaskKind.AgentWork;

        public bool RequiresExecutionContext(
            MissionTask task) => true;

        public Task<string> CreateExecutionContextAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                PreparedRecoveryContext);

        public Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default)
        {
            interruption.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            throw new InvalidOperationException(
                "Cancellation was expected.");
        }
    }

    private sealed class RecoveringPreparedExecutor :
        IMissionTaskExecutor,
        IMissionTaskExecutionContextProvider
    {
        public MissionTaskKind Kind =>
            MissionTaskKind.AgentWork;

        public bool RequiresExecutionContext(
            MissionTask task) => true;

        public Task<string> CreateExecutionContextAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Recovery must reuse the persisted execution context.");

        public Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default)
        {
            if (task.Status !=
                    DomainTaskStatus.Retrying ||
                task.ExecutionContext !=
                    PreparedRecoveryContext)
            {
                return Task.FromResult(
                    TaskExecutionResult.Failed(
                        "Prepared recovery state was not reused.",
                        "Expected Retrying with the original persisted execution context."));
            }

            return Task.FromResult(
                TaskExecutionResult.Succeeded(
                    "Recovered prepared task."));
        }
    }

    private sealed class AlwaysOkValidatorExecutor :
        IMissionTaskExecutor
    {
        public MissionTaskKind Kind =>
            MissionTaskKind.ValidateMission;

        public Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default)
        {
            var result = new ValidationResult(
                "ok",
                "Recovery validation passed.",
                []);

            return Task.FromResult(
                TaskExecutionResult.Succeeded(
                    result.Summary,
                    JsonSerializer.Serialize(
                        new ValidatorExecutionResult(
                            "recovery-self-test-snapshot",
                            result),
                        JsonOptions)));
        }
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
