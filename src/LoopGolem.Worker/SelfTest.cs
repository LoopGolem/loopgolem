using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Agents;
using LoopGolem.Worker.Execution;
using LoopGolem.Worker.Infrastructure;
using Microsoft.Data.Sqlite;
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

            if (!await VerifyStreamingProcessRunnerAsync(
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
                    processRunner))
            {
                return 1;
            }

            if (!VerifyTokenUsageParsing() ||
                !VerifyCodexSessionArguments())
            {
                return 1;
            }

            if (!await VerifyLegacyDatabaseMigrationAsync(root))
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

            if (!await VerifyExecutionTelemetryPersistenceAsync(
                    store,
                    database,
                    created))
            {
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
                    task => task.Definition is null) ||
                persisted.Tasks.Sum(
                    task => task.TokenUsage?.TotalTokens ?? 0) != 240)
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



    private static async Task<bool> VerifyStreamingProcessRunnerAsync(
        ProcessRunner processRunner,
        string workspace)
    {
        var lines = new List<string>();

        var run = await processRunner.RunStreamingAsync(
            "git",
            ["--version"],
            workspace,
            TimeSpan.FromSeconds(30),
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            });

        if (run.ExitCode != 0 ||
            run.TimedOut ||
            lines.Count == 0 ||
            !run.StandardOutput.Contains(
                lines[0],
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Self-test did not stream process stdout while preserving captured output.");
            return false;
        }

        return true;
    }

    private static bool VerifyCodexSessionArguments()
    {
        var ephemeral = CodexPlanningService.BuildArguments(
            "/workspace",
            "gpt-6-luna",
            "low",
            "workspace-write",
            "/schema.json",
            "/output.json",
            CodexSessionRequest.EphemeralFresh);

        var persistent = CodexPlanningService.BuildArguments(
            "/workspace",
            "gpt-6-luna",
            "high",
            "read-only",
            "/schema.json",
            "/output.json",
            CodexSessionRequest.NewPersistent);

        var resumed = CodexPlanningService.BuildArguments(
            "/workspace",
            "gpt-6-luna",
            "high",
            "read-only",
            "/schema.json",
            "/output.json",
            CodexSessionRequest.Resume(
                "local-session",
                "codex-thread"));

        if (!ephemeral.Contains("--ephemeral", StringComparer.Ordinal) ||
            persistent.Contains("--ephemeral", StringComparer.Ordinal) ||
            resumed.Contains("--ephemeral", StringComparer.Ordinal) ||
            !ephemeral.Contains("memories", StringComparer.Ordinal) ||
            !persistent.Contains("memories", StringComparer.Ordinal) ||
            !resumed.Contains("memories", StringComparer.Ordinal) ||
            resumed.Count < 3 ||
            resumed[0] != "exec" ||
            resumed[1] != "resume" ||
            resumed[2] != "codex-thread")
        {
            Console.Error.WriteLine(
                "Self-test Codex session transport arguments are invalid.");
            return false;
        }

        return true;
    }

    private static async Task<bool> VerifyLegacyDatabaseMigrationAsync(
        string root)
    {
        var database = Path.Combine(
            root,
            "state",
            "legacy-migration.db");
        Directory.CreateDirectory(
            Path.GetDirectoryName(database)!);

        await using (var connection =
                     new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = database
                         }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE missions (
                    id TEXT PRIMARY KEY,
                    goal TEXT NOT NULL,
                    workspace_path TEXT NOT NULL,
                    execution_mode TEXT NOT NULL DEFAULT 'ValidateOnly',
                    status TEXT NOT NULL,
                    result TEXT NULL,
                    error TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE mission_tasks (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    kind TEXT NOT NULL DEFAULT 'InspectWorkspace',
                    title TEXT NOT NULL,
                    definition_json TEXT NULL,
                    execution_context TEXT NULL,
                    execution_attempt_count INTEGER NOT NULL DEFAULT 0,
                    token_usage_json TEXT NULL,
                    status TEXT NOT NULL,
                    result TEXT NULL,
                    result_details TEXT NULL,
                    error TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE
                );

                INSERT INTO missions (
                    id, goal, workspace_path, execution_mode, status,
                    created_utc, updated_utc)
                VALUES (
                    'legacy-mission', 'legacy goal', '.', 'Codex', 'Created',
                    '2026-01-01T00:00:00.0000000+00:00',
                    '2026-01-01T00:00:00.0000000+00:00');

                INSERT INTO mission_tasks (
                    id, mission_id, sequence, kind, title, status,
                    created_utc, updated_utc)
                VALUES (
                    'legacy-task', 'legacy-mission', 1, 'InspectWorkspace',
                    'Legacy task', 'Ready',
                    '2026-01-01T00:00:00.0000000+00:00',
                    '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteMissionStore(database);
        await store.InitializeAsync();

        var snapshot = await store.GetAsync("legacy-mission");
        if (snapshot is null ||
            snapshot.Mission.Policy != MissionExecutionPolicy.Default ||
            snapshot.Tasks.Count != 1)
        {
            Console.Error.WriteLine(
                "Self-test did not migrate a legacy mission database safely.");
            return false;
        }

        if ((await store.ListTaskAttemptsAsync("legacy-mission")).Count != 0 ||
            (await store.ListAgentSessionsAsync("legacy-mission")).Count != 0 ||
            (await store.ListAgentTurnsAsync("legacy-mission")).Count != 0 ||
            (await store.ListRecoveryEpisodesAsync("legacy-mission")).Count != 0)
        {
            Console.Error.WriteLine(
                "Self-test legacy migration created unexpected telemetry rows.");
            return false;
        }

        return true;
    }

    private static async Task<bool> VerifyExecutionTelemetryPersistenceAsync(
        SqliteMissionStore store,
        string database,
        MissionSnapshot snapshot)
    {
        var plannerTask = snapshot.Tasks.First(
            task => task.Kind == MissionTaskKind.PlanMission);
        var missionId = snapshot.Mission.Id;
        var now = DateTimeOffset.UtcNow;

        var configured = snapshot with
        {
            Mission = snapshot.Mission with
            {
                Policy = new MissionExecutionPolicy(
                    MaxDeterministicRecoveryCycles: 4,
                    MaxValidationCycles: 5,
                    SessionReuse: SessionReuseMode.Affinity)
            }
        };
        await store.UpdateAsync(configured);

        var attempt = new MissionTaskAttempt(
            Guid.NewGuid().ToString("N"),
            missionId,
            plannerTask.Id,
            1,
            TaskAttemptOutcome.Failed,
            TaskFailureKind.DeterministicCheck,
            "Synthetic deterministic failure.",
            "{\"exitCode\":1}",
            now,
            now.AddMilliseconds(10));
        await store.SaveTaskAttemptAsync(attempt);

        var session = new AgentSession(
            Guid.NewGuid().ToString("N"),
            missionId,
            AgentSessionRole.Supervisor,
            "codex",
            "self-test-thread",
            "gpt-6-luna",
            "high",
            true,
            AgentSessionStatus.Active,
            null,
            1,
            0,
            2,
            now,
            now,
            null,
            null);
        await store.SaveAgentSessionAsync(session);

        var turn = new AgentTurn(
            Guid.NewGuid().ToString("N"),
            missionId,
            session.Id,
            plannerTask.Id,
            AgentTurnPurpose.Recovery,
            1,
            "gpt-6-luna",
            "high",
            new TokenUsage(100, 40, 20, 5, 120)
            {
                CacheWriteInputTokens = 7
            },
            now,
            now.AddMilliseconds(25),
            25,
            true,
            null);
        await store.SaveAgentTurnAsync(turn);

        var episode = new RecoveryEpisode(
            Guid.NewGuid().ToString("N"),
            missionId,
            plannerTask.Id,
            1,
            RecoveryEpisodeStatus.Planning,
            attempt.Id,
            turn.Id,
            ["repair-a", "repair-b"],
            attempt.EvidenceJson,
            now,
            now);
        await store.SaveRecoveryEpisodeAsync(episode);

        var reopened = new SqliteMissionStore(database);
        await reopened.InitializeAsync();

        var persistedMission = await reopened.GetAsync(missionId);
        var attempts = await reopened.ListTaskAttemptsAsync(missionId);
        var sessions = await reopened.ListAgentSessionsAsync(missionId);
        var turns = await reopened.ListAgentTurnsAsync(missionId);
        var episodes = await reopened.ListRecoveryEpisodesAsync(missionId);

        if (persistedMission?.Mission.Policy != configured.Mission.Policy ||
            attempts.Count != 1 ||
            attempts[0] != attempt ||
            sessions.Count != 1 ||
            sessions[0] != session ||
            turns.Count != 1 ||
            turns[0].TokenUsage is not { } usage ||
            usage.InputTokens != 100 ||
            usage.CachedInputTokens != 40 ||
            usage.CacheWriteInputTokens != 7 ||
            usage.OutputTokens != 20 ||
            usage.ReasoningOutputTokens != 5 ||
            usage.TotalTokens != 120 ||
            episodes.Count != 1 ||
            episodes[0].RepairTaskIds.Count != 2 ||
            episodes[0].RepairTaskIds[0] != "repair-a" ||
            episodes[0].RepairTaskIds[1] != "repair-b")
        {
            Console.Error.WriteLine(
                "Self-test did not persist normalized execution telemetry.");
            return false;
        }

        return true;
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

    private static bool VerifyTokenUsageParsing()
    {
        const string modernJson =
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"cache_write_input_tokens\":7,\"output_tokens\":20,\"reasoning_output_tokens\":5}}";

        var usage = CodexPlanningService.ParseTokenUsage(
            modernJson);

        if (usage is null ||
            usage.InputTokens != 100 ||
            usage.CachedInputTokens != 40 ||
            usage.CacheWriteInputTokens != 7 ||
            usage.OutputTokens != 20 ||
            usage.ReasoningOutputTokens != 5 ||
            usage.TotalTokens != 120)
        {
            Console.Error.WriteLine(
                "Self-test could not parse Codex token usage.");
            return false;
        }

        return true;
    }

    private static async Task<bool> VerifyGitChangeCountingAsync(
        ProcessRunner processRunner)
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"loopgolem-git-count-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "baseline.txt"),
                "baseline");

            if (!await InitializeGitAsync(
                    processRunner,
                    workspace))
            {
                Console.Error.WriteLine(
                    "Self-test could not initialize isolated Git counting workspace.");
                return false;
            }

            await File.WriteAllTextAsync(
                Path.Combine(
                    workspace,
                    "git-count-first.txt"),
                "first");
            await File.WriteAllTextAsync(
                Path.Combine(
                    workspace,
                    "git-count-second.txt"),
                "second");

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
            try
            {
                Directory.Delete(
                    workspace,
                    recursive: true);
            }
            catch
            {
            }
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
                        JsonOptions),
                    new TokenUsage(100, 40, 20, 5, 120)));
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
                        JsonOptions),
                    new TokenUsage(50, 20, 10, 2, 60)));
        }
    }
}
