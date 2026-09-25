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

            if (!await VerifyPersistentSupervisorLifecycleAsync(
                    workspace,
                    root))
            {
                return 1;
            }

            if (!await VerifyWorkerSessionAffinityAsync(
                    root))
            {
                return 1;
            }

            if (!await VerifyAutomaticDeterministicRecoveryAsync(
                    root))
            {
                return 1;
            }

            if (!await VerifyRecoveryExhaustionAsync(
                    root))
            {
                return 1;
            }

            if (!await VerifyRecoveryRestartAsync(
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
            var capabilitySnapshot =
                CreateTestCapabilitySnapshot(
                    created.Mission.Id,
                    telemetryNow);

            await store.UpsertTaskAttemptAsync(attempt);
            await store.UpsertAgentSessionAsync(session);
            await store.UpsertAgentTurnAsync(turn);
            await store.UpsertRecoveryCycleAsync(recoveryCycle);
            await store.UpsertCapabilitySnapshotAsync(
                capabilitySnapshot);

            var completed = await orchestrator.RunMissionAsync(
                created.Mission.Id);

            if (completed is null ||
                completed.Mission.Status != MissionStatus.Completed ||
                completed.Tasks.Count != 12 ||
                completed.Tasks.Any(
                    task =>
                        task.Status != DomainTaskStatus.Completed ||
                        task.ExecutionAttemptCount !=
                            (task.Id == telemetryTask.Id
                                ? 2
                                : 1)))
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
            var persistedCapabilities =
                await reopened.GetCapabilitySnapshotAsync(
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

            var persistedFixtureAttempt =
                persistedAttempts.SingleOrDefault(
                    candidate =>
                        candidate.Id == attempt.Id);
            var runtimeAttempts =
                persistedAttempts
                    .Where(candidate =>
                        candidate.Id != attempt.Id)
                    .ToArray();

            if (persistedFixtureAttempt != attempt ||
                runtimeAttempts.Length !=
                    persisted.Tasks.Count ||
                runtimeAttempts.Any(
                    candidate =>
                        candidate.Outcome !=
                            MissionTaskAttemptOutcome.Succeeded) ||
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
                    recoveryCycle.RepairTaskIds) ||
                persistedCapabilities is null ||
                JsonSerializer.Serialize(
                    persistedCapabilities,
                    JsonOptions) !=
                JsonSerializer.Serialize(
                    capabilitySnapshot,
                    JsonOptions))
            {
                Console.Error.WriteLine(
                    "Self-test did not round-trip orchestration telemetry/capabilities.");
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

        var now = DateTimeOffset.UtcNow;
        var sequencingSession = new AgentSession(
            "sequencing-session",
            "protocol-mission",
            AgentSessionRole.Supervisor,
            "gpt-6-luna",
            "high",
            "thread-123",
            AgentSessionStatus.Active,
            null,
            1,
            0,
            null,
            now,
            now,
            now);
        var interruptedTurn = new AgentTurn(
            "interrupted-turn",
            "protocol-mission",
            "protocol-task",
            sequencingSession.Id,
            AgentTurnPurpose.Recovery,
            "gpt-6-luna",
            "high",
            2,
            now,
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            0);

        if (CodexSessionTransport.GetNextTurnNumber(
                sequencingSession,
                [interruptedTurn]) != 3)
        {
            Console.Error.WriteLine(
                "Self-test Codex turn sequencing would reuse an interrupted turn number.");
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

    private static async Task<bool>
        VerifyPersistentSupervisorLifecycleAsync(
            string workspace,
            string root)
    {
        var database = Path.Combine(
            root,
            "state",
            "supervisor-lifecycle.db");
        var store = new SqliteMissionStore(database);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var missionId =
            $"supervisor-{Guid.NewGuid():N}";
        var taskId =
            $"supervisor-task-{Guid.NewGuid():N}";
        var task = new MissionTask(
            taskId,
            missionId,
            1,
            MissionTaskKind.DeterministicWork,
            "Persisted failed build check",
            new PlannedTask(
                "persisted-check",
                "Persisted failed build check",
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
                    "dotnet",
                    ["build"],
                    ".",
                    30)),
            DomainTaskStatus.Failed,
            null,
            null,
            "Build failed in persisted mission state.",
            now,
            now)
        {
            ExecutionAttemptCount = 1
        };
        var mission = new Mission(
            missionId,
            "Repair the mission after a deterministic build failure.",
            workspace,
            MissionExecutionMode.Codex,
            MissionStatus.Running,
            null,
            null,
            now,
            now);

        await store.CreateAsync(
            new MissionSnapshot(
                mission,
                [task]));
        await store.UpsertCapabilitySnapshotAsync(
            CreateTestCapabilitySnapshot(
                missionId,
                now));

        var fakeTransport =
            new FakeSupervisorTransport(store);
        var supervisor =
            new CodexSupervisorSessionService(
                fakeTransport,
                store);

        var planning =
            await supervisor.RunPlanningAsync(
                mission,
                task,
                "gpt-6-luna",
                "high",
                "{}",
                "Initial planning prompt.");

        if (fakeTransport.Requests.Count != 1 ||
            fakeTransport.Requests[0].SessionMode !=
                CodexSessionMode.NewPersistent ||
            fakeTransport.Requests[0].Purpose !=
                AgentTurnPurpose.Planning ||
            string.IsNullOrWhiteSpace(
                planning.ProviderThreadId))
        {
            Console.Error.WriteLine(
                "Self-test did not create a persistent Supervisor for planning.");
            return false;
        }

        var firstSessionId =
            planning.SessionId;

        // Simulate a Worker restart: reopen SQLite and rebuild the
        // Supervisor/transport services from persisted state only.
        var reopenedStore =
            new SqliteMissionStore(database);
        await reopenedStore.InitializeAsync();
        var restartedTransport =
            new FakeSupervisorTransport(
                reopenedStore);
        var restartedSupervisor =
            new CodexSupervisorSessionService(
                restartedTransport,
                reopenedStore);

        var firstRecovery =
            await restartedSupervisor.RunRecoveryAsync(
                mission,
                task.Id,
                "gpt-6-luna",
                "high",
                "{}",
                "Diagnose the deterministic failure.");

        if (restartedTransport.Requests.Count != 1 ||
            restartedTransport.Requests[0].SessionMode !=
                CodexSessionMode.Resume ||
            restartedTransport.Requests[0].SessionId !=
                firstSessionId ||
            firstRecovery.SessionId !=
                firstSessionId)
        {
            Console.Error.WriteLine(
                "Self-test recovery did not resume the persisted planning Supervisor after restart.");
            return false;
        }

        restartedTransport.FailNextResumeAsMissing = true;

        var resetRecovery =
            await restartedSupervisor.RunRecoveryAsync(
                mission,
                task.Id,
                "gpt-6-luna",
                "high",
                "{}",
                "Diagnose the deterministic failure after restart.");

        if (restartedTransport.Requests.Count != 3 ||
            restartedTransport.Requests[1].SessionMode !=
                CodexSessionMode.Resume ||
            restartedTransport.Requests[1].SessionId !=
                firstSessionId ||
            restartedTransport.Requests[2].SessionMode !=
                CodexSessionMode.NewPersistent ||
            restartedTransport.Requests[2].Purpose !=
                AgentTurnPurpose.Recovery ||
            resetRecovery.SessionId ==
                firstSessionId ||
            resetRecovery.ProviderThreadId ==
                planning.ProviderThreadId ||
            !restartedTransport.Requests[2].Prompt.Contains(
                "SUPERVISOR SESSION RESET",
                StringComparison.Ordinal) ||
            !restartedTransport.Requests[2].Prompt.Contains(
                mission.Goal,
                StringComparison.Ordinal) ||
            !restartedTransport.Requests[2].Prompt.Contains(
                task.Title,
                StringComparison.Ordinal) ||
            !restartedTransport.Requests[2].Prompt.Contains(
                "AGENT ENVIRONMENT",
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "Self-test Supervisor reset did not rebuild persisted mission context.");
            return false;
        }

        var sessions =
            await reopenedStore.ListAgentSessionsAsync(
                missionId);
        var oldSession =
            sessions.SingleOrDefault(
                candidate =>
                    candidate.Id ==
                    firstSessionId);
        var newSession =
            sessions.SingleOrDefault(
                candidate =>
                    candidate.Id ==
                    resetRecovery.SessionId);

        if (oldSession?.Status !=
                AgentSessionStatus.Invalidated ||
            oldSession.TerminationReason !=
                "provider_session_not_found" ||
            newSession?.Status !=
                AgentSessionStatus.Active ||
            string.IsNullOrWhiteSpace(
                newSession.ProviderThreadId))
        {
            Console.Error.WriteLine(
                "Self-test Supervisor reset state was not persisted correctly.");
            return false;
        }

        var missingProcess =
            new ProcessRunResult(
                "codex",
                [],
                1,
                false,
                1,
                string.Empty,
                "Session not found: missing-thread");
        var quotaProcess =
            new ProcessRunResult(
                "codex",
                [],
                1,
                false,
                1,
                string.Empty,
                "usage limit reached");

        if (!CodexSupervisorSessionService
                .IsProviderSessionMissing(
                    missingProcess,
                    "missing-thread") ||
            CodexSupervisorSessionService
                .IsProviderSessionMissing(
                    missingProcess,
                    "different-thread") ||
            CodexSupervisorSessionService
                .IsProviderSessionMissing(
                    quotaProcess,
                    "missing-thread"))
        {
            Console.Error.WriteLine(
                "Self-test Supervisor reset classification is too broad.");
            return false;
        }

        return true;
    }

    private static async Task<bool>
        VerifyAutomaticDeterministicRecoveryAsync(
            string root)
    {
        var workspace = Path.Combine(
            root,
            "recovery-success-workspace");
        var database = Path.Combine(
            root,
            "state",
            "recovery-success.db");
        Directory.CreateDirectory(workspace);

        var store =
            new SqliteMissionStore(database);
        await store.InitializeAsync();

        var snapshot =
            CreateRecoveryTestSnapshot(
                workspace,
                maxRecoveryCycles: 3);
        await store.CreateAsync(snapshot);

        var check =
            new RecoveryCheckExecutor(
                alwaysFail: false);
        var repair =
            new RecoveryRepairExecutor();
        var planner =
            new FakeRecoveryPlanner();

        var orchestrator =
            new MissionOrchestrator(
                store,
                [check, repair],
                planner);

        var completed =
            await orchestrator.RunMissionAsync(
                snapshot.Mission.Id);

        if (completed?.Mission.Status !=
                MissionStatus.Completed ||
            check.Calls != 2 ||
            planner.Calls != 1 ||
            check.Definitions.Count != 2 ||
            check.Definitions[0] !=
                check.Definitions[1])
        {
            Console.Error.WriteLine(
                "Self-test deterministic recovery did not repair and rerun the exact original check.");
            return false;
        }

        var originalTaskId =
            snapshot.Tasks.Single().Id;
        var attempts =
            await store.ListTaskAttemptsAsync(
                snapshot.Mission.Id);
        var checkAttempts =
            attempts
                .Where(attempt =>
                    attempt.TaskId ==
                    originalTaskId)
                .OrderBy(attempt =>
                    attempt.AttemptNumber)
                .ToArray();
        var cycles =
            await store.ListRecoveryCyclesAsync(
                snapshot.Mission.Id);

        if (checkAttempts.Length != 2 ||
            checkAttempts[0].Outcome !=
                MissionTaskAttemptOutcome.Failed ||
            checkAttempts[1].Outcome !=
                MissionTaskAttemptOutcome.Succeeded ||
            cycles.Count != 1 ||
            cycles[0].Status !=
                RecoveryCycleStatus.Succeeded ||
            string.IsNullOrWhiteSpace(
                cycles[0].FailureAttemptId) ||
            cycles[0].RepairTaskIds.Count != 1)
        {
            Console.Error.WriteLine(
                "Self-test deterministic recovery telemetry/state did not round-trip.");
            return false;
        }

        return true;
    }

    private static async Task<bool>
        VerifyRecoveryExhaustionAsync(
            string root)
    {
        var workspace = Path.Combine(
            root,
            "recovery-exhaustion-workspace");
        var database = Path.Combine(
            root,
            "state",
            "recovery-exhaustion.db");
        Directory.CreateDirectory(workspace);

        var store =
            new SqliteMissionStore(database);
        await store.InitializeAsync();

        var snapshot =
            CreateRecoveryTestSnapshot(
                workspace,
                maxRecoveryCycles: 2);
        await store.CreateAsync(snapshot);

        var check =
            new RecoveryCheckExecutor(
                alwaysFail: true);
        var repair =
            new RecoveryRepairExecutor();
        var planner =
            new FakeRecoveryPlanner();

        var orchestrator =
            new MissionOrchestrator(
                store,
                [check, repair],
                planner);

        var result =
            await orchestrator.RunMissionAsync(
                snapshot.Mission.Id);
        var cycles =
            await store.ListRecoveryCyclesAsync(
                snapshot.Mission.Id);
        var original =
            result?.Tasks.SingleOrDefault(
                task =>
                    task.Id ==
                    snapshot.Tasks.Single().Id);

        if (result?.Mission.Status !=
                MissionStatus.NeedsHumanAttention ||
            original?.Status !=
                DomainTaskStatus.Escalated ||
            check.Calls != 3 ||
            planner.Calls != 2 ||
            cycles.Count != 2 ||
            cycles[0].Status !=
                RecoveryCycleStatus.Failed ||
            cycles[1].Status !=
                RecoveryCycleStatus.Exhausted)
        {
            Console.Error.WriteLine(
                "Self-test deterministic recovery did not stop at the configured cycle limit.");
            return false;
        }

        return true;
    }

    private static async Task<bool>
        VerifyRecoveryRestartAsync(
            string root)
    {
        var workspace = Path.Combine(
            root,
            "recovery-restart-workspace");
        var database = Path.Combine(
            root,
            "state",
            "recovery-restart.db");
        Directory.CreateDirectory(workspace);

        var store =
            new SqliteMissionStore(database);
        await store.InitializeAsync();

        var snapshot =
            CreateRecoveryTestSnapshot(
                workspace,
                maxRecoveryCycles: 3);
        await store.CreateAsync(snapshot);

        using var interruption =
            new CancellationTokenSource();
        var check =
            new RecoveryCheckExecutor(
                alwaysFail: false);
        var interruptingRepair =
            new InterruptingRecoveryRepairExecutor(
                interruption);
        var planner =
            new FakeRecoveryPlanner();
        var firstOrchestrator =
            new MissionOrchestrator(
                store,
                [check, interruptingRepair],
                planner);

        try
        {
            await firstOrchestrator.RunMissionAsync(
                snapshot.Mission.Id,
                interruption.Token);
            Console.Error.WriteLine(
                "Self-test recovery restart did not interrupt during a repair task.");
            return false;
        }
        catch (OperationCanceledException)
        {
        }

        var persisted =
            await store.GetAsync(
                snapshot.Mission.Id);
        var persistedCycles =
            await store.ListRecoveryCyclesAsync(
                snapshot.Mission.Id);
        var persistedRepair =
            persisted?.Tasks.FirstOrDefault(
                task =>
                    task.Kind ==
                        MissionTaskKind.AgentWork);
        var persistedAttempts =
            await store.ListTaskAttemptsAsync(
                snapshot.Mission.Id);

        if (persisted is null ||
            persisted.Mission.Status !=
                MissionStatus.Running ||
            persisted.Tasks.Single(
                task =>
                    task.Id ==
                    snapshot.Tasks.Single().Id)
                .Status !=
                DomainTaskStatus.RecoveryPending ||
            persistedRepair?.Status !=
                DomainTaskStatus.Running ||
            persistedCycles.Count != 1 ||
            persistedCycles[0].Status !=
                RecoveryCycleStatus.Repairing ||
            !persistedAttempts.Any(
                attempt =>
                    attempt.TaskId ==
                        persistedRepair.Id &&
                    attempt.Outcome ==
                        MissionTaskAttemptOutcome.Interrupted))
        {
            Console.Error.WriteLine(
                "Self-test recovery restart did not persist the in-flight repair state.");
            return false;
        }

        var reopened =
            new SqliteMissionStore(database);
        await reopened.InitializeAsync();

        var resumedCheck =
            new RecoveryCheckExecutor(
                alwaysFail: false);
        var resumedRepair =
            new RecoveryRepairExecutor();
        var noReplan =
            new FailIfCalledRecoveryPlanner();
        var restartedOrchestrator =
            new MissionOrchestrator(
                reopened,
                [resumedCheck, resumedRepair],
                noReplan);

        var completed =
            await restartedOrchestrator.RunMissionAsync(
                snapshot.Mission.Id);
        var finalCycles =
            await reopened.ListRecoveryCyclesAsync(
                snapshot.Mission.Id);
        var finalAttempts =
            await reopened.ListTaskAttemptsAsync(
                snapshot.Mission.Id);

        if (completed?.Mission.Status !=
                MissionStatus.Completed ||
            noReplan.Calls != 0 ||
            resumedCheck.Calls != 1 ||
            finalCycles.Count != 1 ||
            finalCycles[0].Status !=
                RecoveryCycleStatus.Succeeded ||
            finalAttempts.Count(
                attempt =>
                    attempt.TaskId ==
                        persistedRepair.Id) != 2 ||
            !finalAttempts.Any(
                attempt =>
                    attempt.TaskId ==
                        persistedRepair.Id &&
                    attempt.AttemptNumber == 2 &&
                    attempt.Outcome ==
                        MissionTaskAttemptOutcome.Succeeded))
        {
            Console.Error.WriteLine(
                "Self-test recovery did not resume persisted repairs and finish the exact recheck after restart.");
            return false;
        }

        return true;
    }

    private static MissionSnapshot
        CreateRecoveryTestSnapshot(
            string workspace,
            int maxRecoveryCycles)
    {
        var now = DateTimeOffset.UtcNow;
        var missionId =
            $"recovery-{Guid.NewGuid():N}";
        var mission =
            new Mission(
                missionId,
                "Repair a deterministic check without weakening it.",
                workspace,
                MissionExecutionMode.Codex,
                MissionStatus.Running,
                null,
                null,
                now,
                now)
            {
                Policy =
                    MissionPolicy.Default with
                    {
                        MaxRecoveryCycles =
                            maxRecoveryCycles
                    }
            };
        var definition =
            new PlannedTask(
                "exact-recovery-check",
                "Run exact recovery check",
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
                    "dotnet",
                    ["build"],
                    ".",
                    30));
        var task =
            new MissionTask(
                Guid.NewGuid().ToString("N"),
                missionId,
                1,
                MissionTaskKind.DeterministicWork,
                definition.Title,
                definition,
                DomainTaskStatus.Ready,
                null,
                null,
                null,
                now,
                now);

        return new MissionSnapshot(
            mission,
            [task]);
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

    private static MissionCapabilitySnapshot
        CreateTestCapabilitySnapshot(
            string missionId,
            DateTimeOffset capturedAtUtc) =>
        new(
            missionId,
            new ExecutionEnvironmentCapabilities(
                "wsl",
                "linux",
                "SelfTestLinux",
                [
                    new ToolCapability(
                        "git",
                        true,
                        "git version self-test"),
                    new ToolCapability(
                        "dotnet",
                        false,
                        null)
                ]),
            new ExecutionEnvironmentCapabilities(
                "host",
                OperatingSystem.IsWindows()
                    ? "windows"
                    : "linux",
                null,
                [
                    new ToolCapability(
                        "git",
                        true,
                        "git version self-test"),
                    new ToolCapability(
                        "dotnet",
                        true,
                        "10.0.self-test")
                ]),
            capturedAtUtc);

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
        var capabilities =
            CreateTestCapabilitySnapshot(
                mission.Id,
                now);

        var plannerPrompt =
            CodexPlanningService.BuildPlannerPrompt(
                mission,
                capabilities);
        var validatorPrompt =
            CodexPlanningService.BuildValidatorPrompt(
                mission,
                new ValidatorContext(
                    baseCommit,
                    plan,
                    [],
                    1),
                "self-test-snapshot",
                capabilities);
        var workerPrompt =
            CodexPlanningService.BuildWorkerPrompt(
                new PlannedTask(
                    "capability-worker",
                    "Respect agent capabilities",
                    PlannedExecutorKinds.LunaLow,
                    "Make a small implementation change.",
                    ["README.md"],
                    ["README.md"],
                    ["dotnet build"],
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
                        30)),
                capabilities);

        var prompts =
            new[]
            {
                plannerPrompt,
                validatorPrompt,
                workerPrompt
            };

        if (!plannerPrompt.Contains(
                CodexPlanningService.SelfHostingRule,
                StringComparison.Ordinal) ||
            !validatorPrompt.Contains(
                CodexPlanningService.SelfHostingRule,
                StringComparison.Ordinal) ||
            prompts.Any(
                prompt =>
                    !prompt.Contains(
                        "AGENT ENVIRONMENT",
                        StringComparison.Ordinal) ||
                    !prompt.Contains(
                        "DETERMINISTIC HOST ENVIRONMENT",
                        StringComparison.Ordinal) ||
                    !prompt.Contains(
                        "- dotnet: UNAVAILABLE",
                        StringComparison.Ordinal) ||
                    !prompt.Contains(
                        "- dotnet: available (10.0.self-test)",
                        StringComparison.Ordinal)))
        {
            Console.Error.WriteLine(
                "Self-test Codex prompts are missing self-hosting or environment capability policy.");
            return false;
        }

        if (!plannerPrompt.Contains(
                "Never ask Luna Low to execute a probed tool marked UNAVAILABLE",
                StringComparison.Ordinal) ||
            !workerPrompt.Contains(
                "Do not attempt a probed tool marked UNAVAILABLE",
                StringComparison.Ordinal) ||
            !validatorPrompt.Contains(
                "Do not attempt a probed tool marked UNAVAILABLE",
                StringComparison.Ordinal) ||
            !workerPrompt.Contains(
                "contextReuse.recommended",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Self-test Codex prompts do not enforce agent/host capability boundaries.");
            return false;
        }

        return true;
    }

    private static async Task<bool>
        VerifyWorkerSessionAffinityAsync(
            string root)
    {
        var database = Path.Combine(
            root,
            "state",
            "worker-affinity.db");
        var workspace = Path.Combine(
            root,
            "worker-affinity-workspace");
        Directory.CreateDirectory(workspace);

        var store =
            new SqliteMissionStore(database);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var missionId =
            $"worker-affinity-{Guid.NewGuid():N}";
        var policy =
            MissionPolicy.Default with
            {
                SessionReuse =
                    SessionReuseMode.Affinity,
                MaxWorkerSessionMicrotasks = 3,
                MaxWorkerSessionIdleMinutes = 30,
                MaxActiveWorkerSessions = 4
            };
        var mission =
            new Mission(
                missionId,
                "Verify bounded Luna Low session affinity.",
                workspace,
                MissionExecutionMode.Codex,
                MissionStatus.Running,
                null,
                null,
                now,
                now)
            {
                Policy = policy
            };

        var first =
            CreateAffinityTask(
                missionId,
                1,
                "affinity-first",
                [],
                ["src/feature.cs"],
                []);
        var second =
            CreateAffinityTask(
                missionId,
                2,
                "affinity-second",
                ["src/feature.cs"],
                ["src/feature.cs"],
                ["affinity-first"]);
        var third =
            CreateAffinityTask(
                missionId,
                3,
                "affinity-third",
                ["src/feature.cs"],
                ["src/feature.cs"],
                ["affinity-second"]);
        var fourth =
            CreateAffinityTask(
                missionId,
                4,
                "affinity-fourth",
                ["src/feature.cs"],
                ["src/feature.cs"],
                ["affinity-third"]);
        var unrelated =
            CreateAffinityTask(
                missionId,
                5,
                "affinity-unrelated",
                ["docs/other.md"],
                ["docs/other.md"],
                []);

        await store.CreateAsync(
            new MissionSnapshot(
                mission,
                [
                    first,
                    second,
                    third,
                    fourth,
                    unrelated
                ]));

        var transport =
            new FakeWorkerAffinityTransport(
                store);
        var service =
            new CodexWorkerSessionService(
                transport,
                store);

        var firstRun =
            await service.RunWorkAsync(
                mission,
                first,
                CodexPlanningService.WorkerModel,
                CodexPlanningService.WorkerReasoning,
                "{}",
                "first");
        await service.CompleteWorkAsync(
            mission,
            firstRun,
            true,
            true,
            "Useful feature context remains.");

        var secondRun =
            await service.RunWorkAsync(
                mission,
                second,
                CodexPlanningService.WorkerModel,
                CodexPlanningService.WorkerReasoning,
                "{}",
                "second");
        await service.CompleteWorkAsync(
            mission,
            secondRun,
            true,
            true,
            "Direct follow-up context remains.");

        var thirdRun =
            await service.RunWorkAsync(
                mission,
                third,
                CodexPlanningService.WorkerModel,
                CodexPlanningService.WorkerReasoning,
                "{}",
                "third");
        await service.CompleteWorkAsync(
            mission,
            thirdRun,
            true,
            true,
            "Context remains but cap should close the session.");

        var fourthRun =
            await service.RunWorkAsync(
                mission,
                fourth,
                CodexPlanningService.WorkerModel,
                CodexPlanningService.WorkerReasoning,
                "{}",
                "fourth");
        await service.CompleteWorkAsync(
            mission,
            fourthRun,
            true,
            true,
            "New bounded session.");

        var unrelatedRun =
            await service.RunWorkAsync(
                mission,
                unrelated,
                CodexPlanningService.WorkerModel,
                CodexPlanningService.WorkerReasoning,
                "{}",
                "unrelated");
        await service.CompleteWorkAsync(
            mission,
            unrelatedRun,
            true,
            true,
            "Unrelated context should start separately.");

        var modes =
            transport.Requests
                .Take(5)
                .Select(request =>
                    request.SessionMode)
                .ToArray();

        if (!modes.SequenceEqual(
                [
                    CodexSessionMode.NewPersistent,
                    CodexSessionMode.Resume,
                    CodexSessionMode.Resume,
                    CodexSessionMode.NewPersistent,
                    CodexSessionMode.NewPersistent
                ]) ||
            firstRun.SessionId !=
                secondRun.SessionId ||
            firstRun.SessionId !=
                thirdRun.SessionId ||
            fourthRun.SessionId ==
                firstRun.SessionId ||
            unrelatedRun.SessionId ==
                fourthRun.SessionId)
        {
            Console.Error.WriteLine(
                "Self-test Luna Low affinity did not reuse and split sessions as expected.");
            return false;
        }

        var sessions =
            await store.ListAgentSessionsAsync(
                missionId);
        var capped =
            sessions.Single(
                session =>
                    session.Id ==
                    firstRun.SessionId);

        if (capped.Status !=
                AgentSessionStatus.Closed ||
            capped.MicrotaskCount != 3 ||
            capped.TerminationReason !=
                "worker_microtask_cap" ||
            capped.LeaseOwnerTaskId is not null)
        {
            Console.Error.WriteLine(
                "Self-test Luna Low session cap did not close the reused session safely.");
            return false;
        }

        var turns =
            await store.ListAgentTurnsAsync(
                missionId);
        if (turns
                .Where(turn =>
                    turn.SessionId ==
                        firstRun.SessionId)
                .Any(turn =>
                    turn.ContextReuseRecommended !=
                        true ||
                    string.IsNullOrWhiteSpace(
                        turn.ContextReuseReason)))
        {
            Console.Error.WriteLine(
                "Self-test did not persist Luna Low context reuse hints.");
            return false;
        }

        var adjacentScore =
            CodexWorkerSessionService.GetAffinityScore(
                first,
                second,
                [first, second, third]);
        var conflictScore =
            CodexWorkerSessionService.GetAffinityScore(
                first,
                third,
                [first, second, third]);

        if (adjacentScore < 3 ||
            conflictScore != 0)
        {
            Console.Error.WriteLine(
                "Self-test Luna Low affinity scoring did not reject intervening writes.");
            return false;
        }

        var staleNow =
            DateTimeOffset.UtcNow;
        var stale = new AgentSession(
            $"stale-{Guid.NewGuid():N}",
            missionId,
            AgentSessionRole.Worker,
            CodexPlanningService.WorkerModel,
            CodexPlanningService.WorkerReasoning,
            $"stale-thread-{Guid.NewGuid():N}",
            AgentSessionStatus.Active,
            first.Id,
            1,
            1,
            null,
            staleNow,
            staleNow,
            staleNow);
        await store.UpsertAgentSessionAsync(
            stale);

        var disabledMission =
            mission with
            {
                Policy =
                    policy with
                    {
                        SessionReuse =
                            SessionReuseMode.Disabled
                    }
            };
        var disabledRun =
            await service.RunWorkAsync(
                disabledMission,
                unrelated,
                CodexPlanningService.WorkerModel,
                CodexPlanningService.WorkerReasoning,
                "{}",
                "control");
        await service.CompleteWorkAsync(
            disabledMission,
            disabledRun,
            true,
            true,
            "Control mode hint.");

        if (transport.Requests.Last().SessionMode !=
            CodexSessionMode.FreshEphemeral)
        {
            Console.Error.WriteLine(
                "Self-test SessionReuseMode.Disabled did not preserve FreshEphemeral control behavior.");
            return false;
        }

        var affinityAgain =
            await service.RunWorkAsync(
                mission,
                unrelated,
                CodexPlanningService.WorkerModel,
                CodexPlanningService.WorkerReasoning,
                "{}",
                "stale-lease-probe");
        await service.CompleteWorkAsync(
            mission,
            affinityAgain,
            true,
            false,
            "Close after stale lease probe.");

        sessions =
            await store.ListAgentSessionsAsync(
                missionId);
        var invalidatedStale =
            sessions.Single(
                session =>
                    session.Id ==
                    stale.Id);

        if (invalidatedStale.Status !=
                AgentSessionStatus.Invalidated ||
            invalidatedStale.TerminationReason !=
                "stale_worker_lease")
        {
            Console.Error.WriteLine(
                "Self-test did not invalidate a stale Luna Low session lease after restart.");
            return false;
        }

        return true;
    }

    private static MissionTask CreateAffinityTask(
        string missionId,
        int sequence,
        string id,
        IReadOnlyList<string> readFiles,
        IReadOnlyList<string> writeFiles,
        IReadOnlyList<string> dependsOn)
    {
        var now = DateTimeOffset.UtcNow;
        var definition =
            new PlannedTask(
                id,
                id,
                PlannedExecutorKinds.LunaLow,
                $"Execute {id}.",
                readFiles,
                writeFiles,
                [],
                dependsOn,
                new DeterministicOperation(
                    DeterministicOperationKinds.None,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    [],
                    string.Empty,
                    30));

        return new MissionTask(
            Guid.NewGuid().ToString("N"),
            missionId,
            sequence,
            MissionTaskKind.AgentWork,
            id,
            definition,
            DomainTaskStatus.Completed,
            null,
            null,
            null,
            now,
            now);
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

    private sealed class RecoveryCheckExecutor(
        bool alwaysFail) :
        IMissionTaskExecutor
    {
        public MissionTaskKind Kind =>
            MissionTaskKind.DeterministicWork;

        public int Calls { get; private set; }

        public List<string> Definitions { get; } =
            [];

        public Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Definitions.Add(
                JsonSerializer.Serialize(
                    task.Definition,
                    JsonOptions));

            var marker =
                Path.Combine(
                    mission.WorkspacePath,
                    "recovery-marker.txt");

            if (!alwaysFail &&
                File.Exists(marker))
            {
                return Task.FromResult(
                    TaskExecutionResult.Succeeded(
                        "Exact deterministic check passed.",
                        JsonSerializer.Serialize(
                            new ProcessRunResult(
                                "dotnet",
                                ["build"],
                                0,
                                false,
                                5,
                                "Build succeeded.",
                                string.Empty))));
            }

            return Task.FromResult(
                TaskExecutionResult.Failed(
                    "Exact deterministic check failed.",
                    "CS0103: simulated build error.",
                    JsonSerializer.Serialize(
                        new ProcessRunResult(
                            "dotnet",
                            ["build"],
                            1,
                            false,
                            5,
                            "CS0103: simulated build error.",
                            string.Empty)),
                    failureKind:
                        TaskFailureKind.KnownDeterministicFailure));
        }
    }

    private sealed class RecoveryRepairExecutor :
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
                "recovery-test-baseline");

        public async Task<TaskExecutionResult> ExecuteAsync(
            Mission mission,
            MissionTask task,
            CancellationToken cancellationToken = default)
        {
            var path =
                Path.Combine(
                    mission.WorkspacePath,
                    "recovery-marker.txt");

            await File.WriteAllTextAsync(
                path,
                "repaired",
                cancellationToken);

            return TaskExecutionResult.Succeeded(
                "Applied bounded recovery repair.");
        }
    }

    private sealed class InterruptingRecoveryRepairExecutor(
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
                "recovery-test-baseline");

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

    private sealed class FakeRecoveryPlanner :
        IMissionRecoveryPlanner
    {
        public int Calls { get; private set; }

        public Task<RecoveryPlanningResult>
            PlanRecoveryAsync(
                Mission mission,
                MissionTask failedTask,
                MissionTaskAttempt failureAttempt,
                RecoveryCycle cycle,
                CancellationToken cancellationToken = default)
        {
            Calls++;

            var id =
                RecoveryTaskNaming.GetRepairPrefix(
                    failedTask.Id,
                    cycle.CycleNumber) +
                "fix-build";

            var repair =
                new PlannedTask(
                    id,
                    "Repair simulated build failure",
                    PlannedExecutorKinds.LunaLow,
                    "Fix the implementation that causes the deterministic build failure.",
                    [],
                    ["recovery-marker.txt"],
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
                        60));

            return Task.FromResult(
                RecoveryPlanningResult.Succeeded(
                    "Apply one bounded repair.",
                    [repair],
                    null));
        }
    }

    private sealed class FailIfCalledRecoveryPlanner :
        IMissionRecoveryPlanner
    {
        public int Calls { get; private set; }

        public Task<RecoveryPlanningResult>
            PlanRecoveryAsync(
                Mission mission,
                MissionTask failedTask,
                MissionTaskAttempt failureAttempt,
                RecoveryCycle cycle,
                CancellationToken cancellationToken = default)
        {
            Calls++;

            throw new InvalidOperationException(
                "Persisted recovery should not invoke the Supervisor again.");
        }
    }

    private sealed class FakeWorkerAffinityTransport(
        IMissionStore store) : ICodexSessionTransport
    {
        public List<CodexStructuredRunRequest> Requests { get; } = [];

        public async Task<CodexStructuredRunResult>
            RunStructuredAsync(
                CodexStructuredRunRequest request,
                CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var now = DateTimeOffset.UtcNow;
            AgentSession session;
            int turnNumber;

            if (request.SessionMode ==
                CodexSessionMode.Resume)
            {
                var sessions =
                    await store.ListAgentSessionsAsync(
                        request.MissionId,
                        cancellationToken);
                session =
                    sessions.Single(
                        candidate =>
                            candidate.Id ==
                            request.SessionId);
                turnNumber =
                    session.TurnCount + 1;
                session =
                    session with
                    {
                        TurnCount = turnNumber,
                        LeaseOwnerTaskId =
                            request.LeaseOwnerTaskId,
                        LastUsedAtUtc = now,
                        UpdatedAtUtc = now
                    };
            }
            else
            {
                turnNumber = 1;
                var ephemeral =
                    request.SessionMode ==
                    CodexSessionMode.FreshEphemeral;
                session = new AgentSession(
                    $"fake-worker-{Guid.NewGuid():N}",
                    request.MissionId,
                    AgentSessionRole.Worker,
                    request.Model,
                    request.ReasoningEffort,
                    ephemeral
                        ? null
                        : $"fake-worker-thread-{Guid.NewGuid():N}",
                    ephemeral
                        ? AgentSessionStatus.Closed
                        : AgentSessionStatus.Active,
                    request.LeaseOwnerTaskId,
                    turnNumber,
                    0,
                    ephemeral
                        ? "ephemeral"
                        : null,
                    now,
                    now,
                    now);
            }

            await store.UpsertAgentSessionAsync(
                session,
                cancellationToken);

            var turnId =
                $"fake-worker-turn-{Guid.NewGuid():N}";
            var turn = new AgentTurn(
                turnId,
                request.MissionId,
                request.TaskId,
                session.Id,
                AgentTurnPurpose.Work,
                request.Model,
                request.ReasoningEffort,
                turnNumber,
                now,
                now.AddMilliseconds(5),
                5,
                100,
                80,
                0,
                10,
                2,
                110);
            await store.UpsertAgentTurnAsync(
                turn,
                cancellationToken);

            return new CodexStructuredRunResult(
                new ProcessRunResult(
                    "codex",
                    [],
                    0,
                    false,
                    5,
                    string.Empty,
                    string.Empty),
                "{}",
                "{}",
                new TokenUsage(
                    100,
                    80,
                    10,
                    2,
                    110),
                session.Id,
                session.ProviderThreadId,
                turnNumber,
                turnId);
        }
    }

    private sealed class FakeSupervisorTransport(
        IMissionStore store) : ICodexSessionTransport
    {
        public List<CodexStructuredRunRequest> Requests { get; } = [];

        public bool FailNextResumeAsMissing { get; set; }

        public async Task<CodexStructuredRunResult>
            RunStructuredAsync(
                CodexStructuredRunRequest request,
                CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (request.SessionMode ==
                    CodexSessionMode.Resume &&
                FailNextResumeAsMissing)
            {
                FailNextResumeAsMissing = false;

                var knownSessions =
                    await store.ListAgentSessionsAsync(
                        request.MissionId,
                        cancellationToken);
                var expected =
                    knownSessions.Single(
                        candidate =>
                            candidate.Id ==
                            request.SessionId);
                var providerThreadId =
                    expected.ProviderThreadId
                    ?? throw new InvalidOperationException(
                        "Fake Supervisor resume requires a provider thread id.");

                return new CodexStructuredRunResult(
                    new ProcessRunResult(
                        "codex",
                        [],
                        1,
                        false,
                        1,
                        string.Empty,
                        $"Session not found: {providerThreadId}"),
                    string.Empty,
                    "{}",
                    null,
                    request.SessionId!,
                    providerThreadId,
                    0,
                    $"fake-turn-{Guid.NewGuid():N}");
            }

            if (request.SessionMode ==
                CodexSessionMode.NewPersistent)
            {
                var now = DateTimeOffset.UtcNow;
                var sessionId =
                    $"fake-supervisor-{Guid.NewGuid():N}";
                var threadId =
                    $"fake-thread-{Guid.NewGuid():N}";
                var session = new AgentSession(
                    sessionId,
                    request.MissionId,
                    request.Role,
                    request.Model,
                    request.ReasoningEffort,
                    threadId,
                    AgentSessionStatus.Active,
                    null,
                    1,
                    0,
                    null,
                    now,
                    now,
                    now);

                await store.UpsertAgentSessionAsync(
                    session,
                    cancellationToken);

                return Success(
                    session,
                    1);
            }

            var sessions =
                await store.ListAgentSessionsAsync(
                    request.MissionId,
                    cancellationToken);
            var existing =
                sessions.Single(
                    candidate =>
                        candidate.Id ==
                        request.SessionId);
            var turnNumber =
                existing.TurnCount + 1;
            var updated = existing with
            {
                TurnCount = turnNumber,
                LastUsedAtUtc =
                    DateTimeOffset.UtcNow,
                UpdatedAtUtc =
                    DateTimeOffset.UtcNow
            };

            await store.UpsertAgentSessionAsync(
                updated,
                cancellationToken);

            return Success(
                updated,
                turnNumber);
        }

        private static CodexStructuredRunResult Success(
            AgentSession session,
            int turnNumber) =>
            new(
                new ProcessRunResult(
                    "codex",
                    [],
                    0,
                    false,
                    1,
                    string.Empty,
                    string.Empty),
                "{}",
                "{}",
                new TokenUsage(
                    10,
                    5,
                    2,
                    1,
                    12),
                session.Id,
                session.ProviderThreadId,
                turnNumber,
                $"fake-turn-{Guid.NewGuid():N}");
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
