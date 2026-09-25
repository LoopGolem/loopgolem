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
                    processRunner))
            {
                return 1;
            }

            if (!VerifyTokenUsageParsing())
            {
                return 1;
            }

            if (!VerifyCodexSessionProtocol())
            {
                return 1;
            }

            if (!await VerifyProcessStreamingAsync(
                    processRunner,
                    root))
            {
                return 1;
            }

            if (!await VerifyCodexThreadPersistenceAsync(
                    processRunner,
                    workspace,
                    root))
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

            created = created with
            {
                Mission = created.Mission with
                {
                    Policy = new MissionPolicy(
                        MaxRecoveryCycles: 4,
                        MaxValidationCycles: 5,
                        SessionReuse: SessionReuseMode.Affinity)
                }
            };
            await store.UpdateAsync(created);

            var telemetryTask = created.Tasks[0];
            var telemetryNow = DateTimeOffset.UtcNow;
            var attempt = new MissionTaskAttempt(
                $"attempt-{created.Mission.Id}",
                created.Mission.Id,
                telemetryTask.Id,
                1,
                MissionTaskAttemptOutcome.Succeeded,
                "Telemetry persistence probe.",
                null,
                "{\"exitCode\":0}",
                telemetryNow,
                telemetryNow.AddMilliseconds(25));
            var session = new AgentSession(
                $"session-{created.Mission.Id}",
                created.Mission.Id,
                AgentSessionRole.Supervisor,
                "gpt-6-luna",
                "high",
                "self-test-thread",
                AgentSessionStatus.Active,
                null,
                1,
                0,
                null,
                telemetryNow,
                telemetryNow,
                telemetryNow);
            var turn = new AgentTurn(
                $"turn-{created.Mission.Id}",
                created.Mission.Id,
                telemetryTask.Id,
                session.Id,
                AgentTurnPurpose.Planning,
                "gpt-6-luna",
                "high",
                1,
                telemetryNow,
                telemetryNow.AddMilliseconds(20),
                20,
                100,
                40,
                7,
                20,
                5,
                120);
            var recoveryCycle = new RecoveryCycle(
                $"recovery-{created.Mission.Id}",
                created.Mission.Id,
                telemetryTask.Id,
                1,
                RecoveryCycleStatus.Pending,
                attempt.Id,
                turn.Id,
                [telemetryTask.Id],
                telemetryNow,
                telemetryNow);

            await store.UpsertTaskAttemptAsync(attempt);
            await store.UpsertAgentSessionAsync(session);
            await store.UpsertAgentTurnAsync(turn);
            await store.UpsertRecoveryCycleAsync(recoveryCycle);

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
            var persistedAttempts =
                await reopened.ListTaskAttemptsAsync(
                    created.Mission.Id);
            var persistedSessions =
                await reopened.ListAgentSessionsAsync(
                    created.Mission.Id);
            var persistedTurns =
                await reopened.ListAgentTurnsAsync(
                    created.Mission.Id);
            var persistedRecoveryCycles =
                await reopened.ListRecoveryCyclesAsync(
                    created.Mission.Id);

            if (persisted is null ||
                persisted.Mission.Policy != created.Mission.Policy ||
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

            if (persistedAttempts.Count != 1 ||
                persistedAttempts[0] != attempt ||
                persistedSessions.Count != 1 ||
                persistedSessions[0] != session ||
                persistedTurns.Count != 1 ||
                persistedTurns[0] != turn ||
                persistedRecoveryCycles.Count != 1 ||
                persistedRecoveryCycles[0].Id != recoveryCycle.Id ||
                persistedRecoveryCycles[0].MissionId != recoveryCycle.MissionId ||
                persistedRecoveryCycles[0].FailedTaskId != recoveryCycle.FailedTaskId ||
                persistedRecoveryCycles[0].CycleNumber != recoveryCycle.CycleNumber ||
                persistedRecoveryCycles[0].Status != recoveryCycle.Status ||
                persistedRecoveryCycles[0].FailureAttemptId != recoveryCycle.FailureAttemptId ||
                persistedRecoveryCycles[0].RecoveryTurnId != recoveryCycle.RecoveryTurnId ||
                persistedRecoveryCycles[0].CreatedAtUtc != recoveryCycle.CreatedAtUtc ||
                persistedRecoveryCycles[0].UpdatedAtUtc != recoveryCycle.UpdatedAtUtc ||
                !persistedRecoveryCycles[0].RepairTaskIds.SequenceEqual(
                    recoveryCycle.RepairTaskIds))
            {
                Console.Error.WriteLine(
                    "Self-test did not round-trip orchestration telemetry.");
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

    private static bool VerifyCodexSessionProtocol()
    {
        var request = new CodexStructuredRunRequest(
            "protocol-mission",
            "protocol-task",
            AgentSessionRole.Supervisor,
            AgentTurnPurpose.Planning,
            CodexSessionMode.FreshEphemeral,
            null,
            "/workspace",
            "gpt-6-luna",
            "high",
            "read-only",
            "{}",
            "test prompt");

        var ephemeral =
            CodexSessionTransport.BuildArguments(
                request,
                "/workspace",
                "/schema.json",
                "/output.json",
                null);

        if (!ephemeral.Contains("--ephemeral") ||
            ephemeral.Contains("resume") ||
            !ContainsArgumentPair(
                ephemeral,
                "--disable",
                "memories"))
        {
            Console.Error.WriteLine(
                "Self-test Codex ephemeral arguments are invalid.");
            return false;
        }

        var persistent =
            CodexSessionTransport.BuildArguments(
                request with
                {
                    SessionMode =
                        CodexSessionMode.NewPersistent
                },
                "/workspace",
                "/schema.json",
                "/output.json",
                null);

        if (persistent.Contains("--ephemeral") ||
            persistent.Contains("resume"))
        {
            Console.Error.WriteLine(
                "Self-test Codex persistent arguments are invalid.");
            return false;
        }

        var resumed =
            CodexSessionTransport.BuildArguments(
                request with
                {
                    SessionMode =
                        CodexSessionMode.Resume,
                    SessionId = "loop-session"
                },
                "/workspace",
                "/schema.json",
                "/output.json",
                "thread-123");

        var resumeIndex =
            FindArgumentIndex(
                resumed,
                "resume");

        if (resumed.Contains("--ephemeral") ||
            resumeIndex < 0 ||
            resumeIndex + 2 >= resumed.Count ||
            resumed[resumeIndex + 1] !=
                "thread-123" ||
            resumed[resumeIndex + 2] != "-")
        {
            Console.Error.WriteLine(
                "Self-test Codex resume arguments are invalid.");
            return false;
        }

        if (!CodexSessionTransport.TryParseThreadStarted(
                "{\"type\":\"thread.started\",\"thread_id\":\"thread-123\"}",
                out var threadId) ||
            threadId != "thread-123" ||
            CodexSessionTransport.TryParseThreadStarted(
                "{\"type\":\"turn.started\"}",
                out _))
        {
            Console.Error.WriteLine(
                "Self-test could not parse Codex thread.started.");
            return false;
        }

        return true;
    }

    private static int FindArgumentIndex(
        IReadOnlyList<string> arguments,
        string value)
    {
        for (var index = 0;
             index < arguments.Count;
             index++)
        {
            if (arguments[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ContainsArgumentPair(
        IReadOnlyList<string> arguments,
        string first,
        string second)
    {
        for (var index = 0;
             index + 1 < arguments.Count;
             index++)
        {
            if (arguments[index] == first &&
                arguments[index + 1] == second)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool>
        VerifyProcessStreamingAsync(
            ProcessRunner processRunner,
            string root)
    {
        var streamed = new List<string>();

        ProcessRunResult run;
        if (OperatingSystem.IsWindows())
        {
            run = await processRunner.RunStreamingAsync(
                "cmd.exe",
                ["/d", "/s", "/c", "echo first&&echo second"],
                root,
                TimeSpan.FromSeconds(30),
                standardOutputLineHandler:
                    async line =>
                    {
                        await Task.Yield();
                        streamed.Add(line.Trim());
                    });
        }
        else
        {
            run = await processRunner.RunStreamingAsync(
                "/bin/sh",
                ["-c", "printf 'first\\nsecond\\n'"],
                root,
                TimeSpan.FromSeconds(30),
                standardOutputLineHandler:
                    async line =>
                    {
                        await Task.Yield();
                        streamed.Add(line.Trim());
                    });
        }

        if (run.TimedOut ||
            run.ExitCode != 0 ||
            !streamed.SequenceEqual(
                ["first", "second"]))
        {
            Console.Error.WriteLine(
                "Self-test process streaming did not preserve stdout lines.");
            return false;
        }

        return true;
    }

    private static async Task<bool>
        VerifyCodexThreadPersistenceAsync(
            ProcessRunner processRunner,
            string workspace,
            string root)
    {
        var database = Path.Combine(
            root,
            "state",
            "codex-thread-capture.db");
        var store = new SqliteMissionStore(database);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var missionId =
            $"codex-thread-{Guid.NewGuid():N}";
        var taskId =
            $"codex-thread-task-{Guid.NewGuid():N}";
        var snapshot = new MissionSnapshot(
            new Mission(
                missionId,
                "Verify crash-safe Codex thread capture.",
                workspace,
                MissionExecutionMode.Codex,
                MissionStatus.Running,
                null,
                null,
                now,
                now),
            [
                new MissionTask(
                    taskId,
                    missionId,
                    1,
                    MissionTaskKind.InspectWorkspace,
                    "Thread capture placeholder",
                    null,
                    DomainTaskStatus.Ready,
                    null,
                    null,
                    null,
                    now,
                    now)
            ]);

        await store.CreateAsync(snapshot);

        var session = new AgentSession(
            $"session-{Guid.NewGuid():N}",
            missionId,
            AgentSessionRole.Supervisor,
            "gpt-6-luna",
            "high",
            null,
            AgentSessionStatus.Active,
            null,
            0,
            0,
            null,
            now,
            now,
            now);

        await store.UpsertAgentSessionAsync(session);

        var transport = new CodexSessionTransport(
            processRunner,
            new CodexCliService(processRunner),
            store);

        var captured =
            await transport.CaptureThreadStartedAsync(
                CodexSessionMode.NewPersistent,
                session,
                "thread-self-test");

        var reopened =
            new SqliteMissionStore(database);
        await reopened.InitializeAsync();
        var sessions =
            await reopened.ListAgentSessionsAsync(
                missionId);
        var persisted =
            sessions.SingleOrDefault(
                candidate =>
                    candidate.Id == session.Id);

        if (captured.ProviderThreadId !=
                "thread-self-test" ||
            persisted?.ProviderThreadId !=
                "thread-self-test")
        {
            Console.Error.WriteLine(
                "Self-test did not persist thread.started immediately.");
            return false;
        }

        try
        {
            await transport.CaptureThreadStartedAsync(
                CodexSessionMode.Resume,
                captured,
                "wrong-thread");
            Console.Error.WriteLine(
                "Self-test accepted a mismatched resumed Codex thread.");
            return false;
        }
        catch (InvalidDataException)
        {
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
