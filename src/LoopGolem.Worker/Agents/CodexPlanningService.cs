using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Core.Protocol;
using LoopGolem.Orchestrator;
using LoopGolem.Worker.Infrastructure;

namespace LoopGolem.Worker.Agents;

public sealed class CodexPlanningService(
    ProcessRunner processRunner,
    CodexCliService runtime,
    CodexSupervisorSessionService supervisor,
    CodexWorkerSessionService workerSessions,
    CodexValidatorSessionService validatorSessions,
    EnvironmentCapabilityService capabilityService) :
    IMissionRecoveryPlanner
{

    public const string PlannerModel = "gpt-6-luna";
    public const string PlannerReasoning = "high";
    public const string WorkerModel = "gpt-6-luna";
    public const string WorkerReasoning = "low";

    internal const string SelfHostingRule =
        "SELF-HOSTING RULE: if this mission modifies LoopGolem.Core, " +
        "LoopGolem.Orchestrator, or LoopGolem.Worker, the currently running " +
        "Worker will NOT hot-reload those changes. Do not make later tasks " +
        "depend on newly implemented Worker runtime behavior becoming active " +
        "in this same mission. Source/build/test checks may launch newly built " +
        "child processes, but the current orchestrator process remains on its " +
        "original binary.";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly GitSnapshotService _snapshots = new(processRunner);

    public async Task<TaskExecutionResult> PlanAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var status = await runtime.GetStatusAsync(cancellationToken);
        var runtimeError = ValidateRuntime(status);
        if (runtimeError is not null)
        {
            return runtimeError;
        }

        var capabilities =
            await capabilityService.GetOrCaptureAsync(
                mission,
                status,
                cancellationToken);

        var cleanError = await EnsureCleanGitAsync(
            mission.WorkspacePath,
            cancellationToken);
        if (cleanError is not null)
        {
            return cleanError;
        }

        var baseCommit = await _snapshots.GetHeadCommitAsync(
            mission.WorkspacePath,
            cancellationToken);

        var run =
            await supervisor.RunPlanningAsync(
                mission,
                task,
                PlannerModel,
                PlannerReasoning,
                PlannerPlannerWorkerSchema,
                BuildPlannerPrompt(
                    mission,
                    capabilities),
                cancellationToken);

        DevelopmentDiagnostics.Write(
            "planner.raw",
            mission.Id,
            run.FinalMessage);

        if (run.Process.TimedOut)
        {
            return TaskExecutionResult.Failed(
                "Planner timed out.",
                "GPT-6 Luna High exceeded the planner timeout.",
                run.Details, run.TokenUsage);
        }

        if (run.Process.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                $"Planner exited with code {run.Process.ExitCode}.",
                GetProcessError(run.Process),
                run.Details, run.TokenUsage);
        }

        MissionPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<MissionPlan>(
                run.FinalMessage,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Planner returned invalid JSON.",
                exception.Message,
                run.Details, run.TokenUsage);
        }

        if (plan is null)
        {
            return TaskExecutionResult.Failed(
                "Planner returned no mission plan.",
                "The structured planner response was empty.",
                run.Details, run.TokenUsage);
        }

        var validationError = MissionPlanValidator.Validate(plan);
        if (validationError is not null)
        {
            return TaskExecutionResult.Failed(
                "Planner returned an invalid mission plan.",
                validationError,
                run.Details, run.TokenUsage);
        }

        return TaskExecutionResult.Succeeded(
            plan.Summary,
            JsonSerializer.Serialize(
                new PlannerResult(baseCommit, plan)
                {
                    SupervisorProviderThreadId =
                        run.ProviderThreadId
                },
                JsonOptions),
            run.TokenUsage);
    }

    public async Task<RecoveryPlanningResult>
        PlanRecoveryAsync(
            Mission mission,
            MissionTask failedTask,
            MissionTaskAttempt failureAttempt,
            RecoveryCycle cycle,
            CancellationToken cancellationToken = default)
    {
        var status =
            await runtime.GetStatusAsync(
                cancellationToken);
        var runtimeError =
            ValidateRuntime(status);

        if (runtimeError is not null)
        {
            return RecoveryPlanningResult.Failed(
                runtimeError.Summary,
                runtimeError.Error ??
                    runtimeError.Summary);
        }

        var capabilities =
            await capabilityService.GetOrCaptureAsync(
                mission,
                status,
                cancellationToken);

        var run =
            await supervisor.RunRecoveryAsync(
                mission,
                failedTask.Id,
                PlannerModel,
                PlannerReasoning,
                RecoverySchema,
                BuildRecoveryPrompt(
                    mission,
                    failedTask,
                    failureAttempt,
                    cycle,
                    capabilities),
                cancellationToken);

        DevelopmentDiagnostics.Write(
            $"recovery.raw:{cycle.CycleNumber}",
            mission.Id,
            run.FinalMessage);

        if (run.Process.TimedOut)
        {
            return RecoveryPlanningResult.Failed(
                "Recovery planner timed out.",
                "GPT-6 Luna High exceeded the recovery planner timeout.",
                run.TurnId);
        }

        if (run.Process.ExitCode != 0)
        {
            return RecoveryPlanningResult.Failed(
                $"Recovery planner exited with code {run.Process.ExitCode}.",
                GetProcessError(run.Process),
                run.TurnId);
        }

        RecoveryPlan? plan;
        try
        {
            plan =
                JsonSerializer.Deserialize<RecoveryPlan>(
                    run.FinalMessage,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            return RecoveryPlanningResult.Failed(
                "Recovery planner returned invalid JSON.",
                exception.Message,
                run.TurnId);
        }

        if (plan is null)
        {
            return RecoveryPlanningResult.Failed(
                "Recovery planner returned no repair plan.",
                "The structured recovery response was empty.",
                run.TurnId);
        }

        var validationError =
            MissionPlanValidator.ValidateTasks(
                plan.Tasks);

        if (validationError is not null)
        {
            return RecoveryPlanningResult.Failed(
                "Recovery planner returned an invalid repair plan.",
                validationError,
                run.TurnId);
        }

        if (plan.Tasks.Count == 0)
        {
            return RecoveryPlanningResult.Failed(
                "Recovery planner returned no repair tasks.",
                "A deterministic failure requires at least one bounded repair task.",
                run.TurnId);
        }

        if (plan.Tasks.Count > 12)
        {
            return RecoveryPlanningResult.Failed(
                "Recovery planner returned too many repair tasks.",
                "A recovery cycle may contain at most 12 micro-repairs.",
                run.TurnId);
        }

        var expectedPrefix =
            RecoveryTaskNaming.GetRepairPrefix(
                failedTask.Id,
                cycle.CycleNumber);

        foreach (var repair in plan.Tasks)
        {
            if (repair.Executor !=
                    PlannedExecutorKinds.LunaLow)
            {
                return RecoveryPlanningResult.Failed(
                    "Recovery planner returned a non-Luna repair.",
                    $"Repair task '{repair.Id}' must use executor 'luna_low'.",
                    run.TurnId);
            }

            if (!repair.Id.StartsWith(
                    expectedPrefix,
                    StringComparison.Ordinal))
            {
                return RecoveryPlanningResult.Failed(
                    "Recovery planner returned an invalid repair id.",
                    $"Every repair id in recovery cycle {cycle.CycleNumber} must start with '{expectedPrefix}'.",
                    run.TurnId);
            }
        }

        return RecoveryPlanningResult.Succeeded(
            plan.Summary,
            plan.Tasks,
            run.TurnId);
    }

    public async Task<TaskExecutionResult> ValidateAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        if (task.Definition is null ||
            task.Kind != MissionTaskKind.ValidateMission)
        {
            return TaskExecutionResult.Failed(
                "Validator task definition is missing.",
                "LoopGolem did not provide validator context.");
        }

        ValidatorContext? context;
        try
        {
            context = JsonSerializer.Deserialize<ValidatorContext>(
                task.Definition.Prompt,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Validator context is invalid.",
                exception.Message);
        }

        if (context is null ||
            string.IsNullOrWhiteSpace(context.BaseCommit))
        {
            return TaskExecutionResult.Failed(
                "Validator context is incomplete.",
                "The base commit or original plan is missing.");
        }

        var status =
            await runtime.GetStatusAsync(
                cancellationToken);
        var runtimeError =
            ValidateRuntime(status);
        if (runtimeError is not null)
        {
            return runtimeError;
        }

        var capabilities =
            await capabilityService.GetOrCaptureAsync(
                mission,
                status,
                cancellationToken);

        string snapshotCommit;
        try
        {
            snapshotCommit =
                await _snapshots.CreateSnapshotCommitAsync(
                    mission.WorkspacePath,
                    context.BaseCommit,
                    mission.Id,
                    cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return TaskExecutionResult.Failed(
                "Could not create the validation snapshot.",
                exception.Message);
        }

        var run =
            await validatorSessions.RunValidationAsync(
                mission,
                task,
                PlannerModel,
                PlannerReasoning,
                ValidatorSchema,
                BuildValidatorPrompt(
                    mission,
                    context,
                    snapshotCommit,
                    capabilities),
                cancellationToken);

        DevelopmentDiagnostics.Write(
            $"validator.raw:{context.Cycle}",
            mission.Id,
            run.FinalMessage);

        if (run.Process.TimedOut)
        {
            return await RejectValidationAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Validator timed out.",
                    "GPT-6 Luna High exceeded the validator timeout.",
                    run.Details,
                    run.TokenUsage),
                "validator_timeout");
        }

        if (run.Process.ExitCode != 0)
        {
            return await RejectValidationAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    $"Validator exited with code {run.Process.ExitCode}.",
                    GetProcessError(run.Process),
                    run.Details,
                    run.TokenUsage),
                "validator_process_failed");
        }

        ValidationResult? result;
        try
        {
            result =
                JsonSerializer.Deserialize<ValidationResult>(
                    run.FinalMessage,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            return await RejectValidationAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Validator returned invalid JSON.",
                    exception.Message,
                    run.Details,
                    run.TokenUsage),
                "validator_invalid_json");
        }

        if (result is null ||
            result.Status is not ("ok" or "not_ok"))
        {
            return await RejectValidationAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Validator returned an unsupported result.",
                    result?.Status ??
                        "The structured result was empty.",
                    run.Details,
                    run.TokenUsage),
                "validator_unsupported_result");
        }

        if (result.Status == "ok" &&
            result.Tasks.Count != 0)
        {
            return await RejectValidationAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Validator returned correction tasks with status ok.",
                    "An ok validation must return an empty task list.",
                    run.Details,
                    run.TokenUsage),
                "validator_invalid_ok_result");
        }

        if (result.Status == "not_ok")
        {
            var taskError =
                MissionPlanValidator.ValidateTasks(
                    result.Tasks);

            if (taskError is not null)
            {
                return await RejectValidationAsync(
                    mission,
                    run,
                    TaskExecutionResult.Failed(
                        "Validator returned an invalid correction plan.",
                        taskError,
                        run.Details,
                        run.TokenUsage),
                    "validator_invalid_correction_plan");
            }

            if (result.Tasks.Count == 0)
            {
                return await RejectValidationAsync(
                    mission,
                    run,
                    TaskExecutionResult.Failed(
                        "Validator returned not_ok without correction tasks.",
                        "At least one correction task is required.",
                        run.Details,
                        run.TokenUsage),
                    "validator_empty_correction_plan");
            }

            var expectedPrefix =
                $"fix{context.Cycle}_";

            if (result.Tasks.Any(
                    correction =>
                        !correction.Id.StartsWith(
                            expectedPrefix,
                            StringComparison.Ordinal)))
            {
                return await RejectValidationAsync(
                    mission,
                    run,
                    TaskExecutionResult.Failed(
                        "Validator returned correction ids outside the required namespace.",
                        $"Every correction id in cycle {context.Cycle} must start with '{expectedPrefix}'.",
                        run.Details,
                        run.TokenUsage),
                    "validator_invalid_correction_namespace");
            }
        }

        var maxValidationCycles =
            Math.Max(
                1,
                mission.Policy.MaxValidationCycles);
        var keepActive =
            result.Status == "not_ok" &&
            context.Cycle <
                maxValidationCycles;

        await validatorSessions.CompleteValidationAsync(
            mission.Id,
            run,
            accepted: true,
            keepActive: keepActive,
            reason: keepActive
                ? "validation_cycle_complete"
                : result.Status == "ok"
                    ? "validation_complete"
                    : "validation_limit_reached",
            cancellationToken: CancellationToken.None);

        return TaskExecutionResult.Succeeded(
            result.Summary,
            JsonSerializer.Serialize(
                new ValidatorExecutionResult(
                    snapshotCommit,
                    result),
                JsonOptions),
            run.TokenUsage);
    }

    private async Task<TaskExecutionResult>
        RejectValidationAsync(
            Mission mission,
            CodexStructuredRunResult run,
            TaskExecutionResult failure,
            string reason)
    {
        await validatorSessions.CompleteValidationAsync(
            mission.Id,
            run,
            accepted: false,
            keepActive: false,
            reason: reason,
            cancellationToken: CancellationToken.None);

        return failure;
    }

    public async Task<TaskExecutionResult> ExecuteMicroTaskAsync(
        Mission mission,
        MissionTask task,
        CancellationToken cancellationToken = default)
    {
        var definition = task.Definition;
        if (definition is null ||
            definition.Executor != PlannedExecutorKinds.LunaLow)
        {
            return TaskExecutionResult.Failed(
                "Luna Low task definition is missing.",
                "The planner did not provide a valid Luna Worker microtask.");
        }

        var status = await runtime.GetStatusAsync(cancellationToken);
        var runtimeError = ValidateRuntime(status);
        if (runtimeError is not null)
        {
            return runtimeError;
        }

        var capabilities =
            await capabilityService.GetOrCaptureAsync(
                mission,
                status,
                cancellationToken);

        if (string.IsNullOrWhiteSpace(task.ExecutionContext))
        {
            return TaskExecutionResult.Failed(
                "Luna Worker execution baseline is missing.",
                "The task was not prepared for crash-safe execution.");
        }

        GitWorkspaceSnapshot? before;
        try
        {
            before = JsonSerializer.Deserialize<GitWorkspaceSnapshot>(
                task.ExecutionContext,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return TaskExecutionResult.Failed(
                "Luna Worker execution baseline is invalid.",
                exception.Message);
        }

        if (before is null)
        {
            return TaskExecutionResult.Failed(
                "Luna Worker execution baseline is empty.",
                "The persisted workspace snapshot could not be restored.");
        }

        var run = await workerSessions.RunWorkAsync(
            mission,
            task,
            WorkerModel,
            mission.Policy.EffectiveWorkerReasoningEffort,
            PlannerWorkerSchema,
            BuildWorkerPrompt(
                definition,
                capabilities),
            cancellationToken);

        DevelopmentDiagnostics.Write(
            $"worker.raw:{definition.Id}",
            mission.Id,
            run.FinalMessage);

        if (run.Process.TimedOut)
        {
            return await FinalizeWorkerRunAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Luna Worker microtask timed out.",
                    "The microtask exceeded the one-hour timeout.",
                    run.Details,
                    run.TokenUsage),
                false,
                "The worker timed out; its context is not safe to reuse.",
                cancellationToken);
        }

        if (run.Process.ExitCode != 0)
        {
            return await FinalizeWorkerRunAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    $"Luna Worker exited with code {run.Process.ExitCode}.",
                    GetProcessError(run.Process),
                    run.Details,
                    run.TokenUsage),
                false,
                "The worker process failed; its context is not safe to reuse.",
                cancellationToken);
        }

        CodexAgentOutcome? outcome;
        try
        {
            outcome = JsonSerializer.Deserialize<CodexAgentOutcome>(
                run.FinalMessage,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return await FinalizeWorkerRunAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Luna Worker returned invalid structured output.",
                    exception.Message,
                    run.Details,
                    run.TokenUsage),
                false,
                "The worker returned invalid structured output.",
                cancellationToken);
        }

        if (outcome is null)
        {
            return await FinalizeWorkerRunAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Luna Worker returned no structured output.",
                    "The final response was empty.",
                    run.Details,
                    run.TokenUsage),
                false,
                "The worker returned no reusable structured context hint.",
                cancellationToken);
        }

        GitWorkspaceSnapshot after;
        try
        {
            after = await GitWorkspaceSnapshot.CaptureAsync(
                processRunner,
                mission.WorkspacePath,
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return await FinalizeWorkerRunAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Could not verify the workspace after the microtask.",
                    exception.Message,
                    run.Details,
                    run.TokenUsage),
                false,
                "Deterministic post-task verification failed.",
                cancellationToken);
        }

        var changedByTask = after.ChangesSince(before);
        var allowed = definition.WriteFiles
            .Select(GitWorkspaceSnapshot.Normalize)
            .ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

        var violations = changedByTask
            .Where(path => !allowed.Contains(path))
            .ToArray();

        if (violations.Length > 0)
        {
            return await FinalizeWorkerRunAsync(
                mission,
                run,
                TaskExecutionResult.Failed(
                    "Luna Low modified files outside its write allowlist.",
                    string.Join(", ", violations),
                    run.Details,
                    run.TokenUsage),
                false,
                "The worker violated its write allowlist.",
                cancellationToken);
        }

        var result = outcome.Outcome switch
        {
            "blocked" => TaskExecutionResult.Failed(
                outcome.Summary,
                string.IsNullOrWhiteSpace(outcome.Blocker)
                    ? "Luna Low reported a blocker."
                    : outcome.Blocker,
                run.Details,
                run.TokenUsage),
            "changed" when changedByTask.Count == 0 =>
                TaskExecutionResult.Failed(
                    "Luna Low reported changes, but no file changed.",
                    "The structured result disagrees with deterministic Git verification.",
                    run.Details,
                    run.TokenUsage),
            "changed" => TaskExecutionResult.Succeeded(
                outcome.Summary,
                run.Details,
                run.TokenUsage),
            "already_satisfied"
                when changedByTask.Count > 0 &&
                     task.Status != LoopGolem.Core.Domain.TaskStatus.Retrying =>
                TaskExecutionResult.Failed(
                    "Luna Low reported no edit was needed, but files changed.",
                    string.Join(", ", changedByTask),
                    run.Details,
                    run.TokenUsage),
            "already_satisfied" => TaskExecutionResult.Succeeded(
                outcome.Summary,
                run.Details,
                run.TokenUsage),
            _ => TaskExecutionResult.Failed(
                "Luna Worker returned an unsupported outcome.",
                outcome.Outcome,
                run.Details,
                run.TokenUsage)
        };

        return await FinalizeWorkerRunAsync(
            mission,
            run,
            result,
            result.Success &&
                outcome.ContextReuse.Recommended,
            result.Success
                ? outcome.ContextReuse.Reason
                : "The worker task was not accepted by deterministic verification.",
            cancellationToken);
    }

    private async Task<TaskExecutionResult>
        FinalizeWorkerRunAsync(
            Mission mission,
            CodexStructuredRunResult run,
            TaskExecutionResult result,
            bool contextReuseRecommended,
            string contextReuseReason,
            CancellationToken cancellationToken)
    {
        await workerSessions.CompleteWorkAsync(
            mission,
            run,
            result.Success,
            contextReuseRecommended,
            contextReuseReason,
            cancellationToken);

        return result;
    }

    internal static TokenUsage? ParseTokenUsage(
        string jsonLines) =>
        CodexSessionTransport.ParseTokenUsage(
            jsonLines);

    internal static string BuildPlannerPrompt(
        Mission mission,
        MissionCapabilitySnapshot capabilities) =>
        $"""
        You are the LoopGolem mission planner running as GPT-6 Luna High.
        You may inspect the entire repository, but you must not modify it.

        USER GOAL:
        {mission.Goal}

        EXECUTION CAPABILITIES:
        {EnvironmentCapabilityService.FormatForPrompt(capabilities)}

        Produce only the structured execution plan required by the schema.

        COMMON ENVELOPE RULES:
        - Set outcome to "plan".
        - Set checks to an empty array.
        - Set blocker to an empty string.
        - Set contextReuse.recommended=false and contextReuse.reason to an empty string.
        - tasks and finalChecks carry the actual planning result.

        RULES:
        - Prefer many small, independently verifiable microtasks over broad tasks.
        - Every task id must be short, unique, stable, and referenced by dependsOn.
        - Use executor "deterministic" whenever the operation is exact and mechanical.
        - Deterministic operations are write_file, create_directory, rename_path, or run_command.
        - run_command is direct process execution on the DETERMINISTIC HOST: executable plus arguments, never a shell command string.
        - Luna Low runs in the AGENT ENVIRONMENT, not on the deterministic host.
        - Never ask Luna Low to execute a probed tool marked UNAVAILABLE in the agent environment.
        - Do not put a host-only tool command into a Luna Low acceptanceChecks list.
        - When a required check uses a tool available on the host but unavailable in the agent environment, schedule that check as deterministic host work instead of asking Luna Low to run it.
        - Do not infer that an unprobed tool is unavailable.
        - Use executor "luna_low" when implementation judgment is required.
        - Luna Low receives only its microtask prompt, readFiles, writeFiles, and acceptanceChecks.
        - Make readFiles and writeFiles precise repository-relative paths.
        - Luna Low may write ONLY writeFiles; include every file it must modify.
        - Use dependsOn whenever a task requires files or state produced by another task.
        - Set rerunAfterRepair=true only for a deterministic prerequisite that is intentionally safe and idempotent to repeat after a source repair and refreshes derived artifacts consumed by downstream dependent checks (for example a build). Set it false otherwise.
        - Never use rerunAfterRepair=true to authorize replay of a command with externally visible or non-idempotent side effects.
        - Never request a model above GPT-6 Luna. Human attention is preferred.
        - Keep each Luna Low prompt self-contained and small.
        - For Luna Low set rerunAfterRepair=false, set deterministic.kind to "none", and leave unused deterministic strings empty.
        - For deterministic tasks the deterministic object must fully specify the one operation.
        - finalChecks lists repository-level checks that the final GPT-6 Luna High validator must review.
        - {SelfHostingRule}
        """;

    internal static string BuildRecoveryPrompt(
        Mission mission,
        MissionTask failedTask,
        MissionTaskAttempt failureAttempt,
        RecoveryCycle cycle,
        MissionCapabilitySnapshot capabilities)
    {
        var evidence =
            failureAttempt.EvidenceJson ??
            failedTask.ResultDetails ??
            string.Empty;

        const int maxEvidenceCharacters = 16000;
        if (evidence.Length >
            maxEvidenceCharacters)
        {
            evidence =
                evidence[..maxEvidenceCharacters] +
                Environment.NewLine +
                "... [evidence truncated for recovery prompt]";
        }

        var repairPrefix =
            RecoveryTaskNaming.GetRepairPrefix(
                failedTask.Id,
                cycle.CycleNumber);

        return $"""
        You are the persistent LoopGolem mission Supervisor running as GPT-6 Luna High.
        A deterministic host check completed and failed with known evidence.
        You are planning a bounded repair cycle. You may inspect the repository, but you must not modify it.

        ORIGINAL USER GOAL:
        {mission.Goal}

        RECOVERY CYCLE:
        {cycle.CycleNumber} of {mission.Policy.MaxRecoveryCycles}

        FAILED TASK:
        {JsonSerializer.Serialize(failedTask.Definition, JsonOptions)}

        FAILED TASK EXECUTION CONTEXT:
        {failedTask.ExecutionContext ?? "(none)"}

        FAILURE SUMMARY:
        {failureAttempt.Summary ?? failedTask.Result ?? "(none)"}

        FAILURE ERROR:
        {failureAttempt.Error ?? failedTask.Error ?? "(none)"}

        DETERMINISTIC FAILURE EVIDENCE:
        {evidence}

        EXECUTION CAPABILITIES:
        {EnvironmentCapabilityService.FormatForPrompt(capabilities)}

        Produce only the structured repair plan required by the schema.

        RECOVERY RULES:
        - Diagnose the concrete cause of the deterministic failure from repository state and the evidence above.
        - Return the smallest useful set of repair microtasks.
        - Every repair task MUST use executor "luna_low".
        - Every repair id MUST start with "{repairPrefix}".
        - A recovery cycle may contain at most 12 repair tasks.
        - Repair dependencies may refer only to other repair tasks in this response.
        - Keep each repair task narrow and independently understandable.
        - Use precise repository-relative readFiles and writeFiles.
        - Include every file a repair may modify in writeFiles.
        - For every repair set rerunAfterRepair=false, set deterministic.kind to "none", and leave unused deterministic strings empty.
        - Luna Low runs in the AGENT ENVIRONMENT. Never require a probed tool marked UNAVAILABLE there.
        - Do not put host-only commands into Luna Low acceptanceChecks.
        - The exact failed deterministic check will be rerun automatically by LoopGolem after all repairs complete.
        - NEVER change, weaken, replace, skip, or work around the failed check. Fix the implementation that caused it to fail.
        - Do not commit, push, create branches, or rewrite Git history.
        - Never request a model above GPT-6 Luna. Human attention is preferred after the configured recovery limit.
        - {SelfHostingRule}
        """;
    }

    internal static string BuildValidatorPrompt(
        Mission mission,
        ValidatorContext context,
        string snapshotCommit,
        MissionCapabilitySnapshot capabilities)
    {
        var planJson = JsonSerializer.Serialize(
            context.Plan,
            JsonOptions);

        return $"""
        You are the LoopGolem final validator running as GPT-6 Luna High.
        You may inspect the entire repository, but you must not modify it.

        ORIGINAL USER GOAL:
        {mission.Goal}

        ORIGINAL PLAN:
        {planJson}

        PRIOR CORRECTION TASKS:
        {JsonSerializer.Serialize(context.CorrectionHistory, JsonOptions)}

        BASE COMMIT:
        {context.BaseCommit}

        SNAPSHOT COMMIT:
        {snapshotCommit}

        VALIDATION CYCLE:
        {context.Cycle}

        EXECUTION CAPABILITIES:
        {EnvironmentCapabilityService.FormatForPrompt(capabilities)}

        VALIDATION RULES:
        - You execute inside the AGENT ENVIRONMENT. Do not attempt a probed tool marked UNAVAILABLE there.
        - Deterministic host checks run before validation; reaching this validator means the preceding scheduled deterministic checks completed successfully.
        - Do not pretend to execute a host-only tool from the agent environment.
        - Do not infer that an unprobed tool is unavailable.
        - Review the actual implementation, not worker claims.
        - This Validator thread may persist across correction cycles, but prior Validator conclusions are context, not authority. Re-inspect the current snapshot independently on every cycle.
        - Start with: git diff --stat {context.BaseCommit}..{snapshotCommit}
        - Inspect the full diff with: git diff {context.BaseCommit}..{snapshotCommit}
        - Read any repository files needed to judge correctness.
        - Check the original user goal, every planned task, acceptance criteria, and finalChecks.
        - The snapshot commit is an unreachable LoopGolem-created Git object. It does not move or modify the user's branch.
        - Return status "ok" only when the snapshot satisfies the original goal and no correction is necessary.
        - Otherwise return status "not_ok" with the smallest set of correction microtasks needed.
        - Prefer small independent correction tasks.
        - Use deterministic operations for exact mechanical corrections and luna_low for code or prose requiring judgment.
        - Correction task readFiles/writeFiles must be precise repository-relative paths.
        - Correction dependencies may refer only to other correction tasks in this response.
        - Use unique correction ids prefixed with "fix{context.Cycle}_".
        - {SelfHostingRule}
        - Never request or assume a model above GPT-6 Luna. If the work is too complex to validate safely, use not_ok with bounded corrective tasks; LoopGolem will stop for human attention after its cycle limit.
        - For luna_low set rerunAfterRepair=false and deterministic.kind to "none".
        - For deterministic correction tasks, set rerunAfterRepair=true only when the task is an intentionally safe, idempotent artifact-refresh prerequisite for a downstream dependent check; otherwise false.
        - Do not commit, push, create branches, or modify files.
        """;
    }

    internal static string BuildWorkerPrompt(
        PlannedTask task,
        MissionCapabilitySnapshot capabilities)
    {
        static string Lines(IEnumerable<string> values) =>
            string.Join(Environment.NewLine, values.Select(value => $"- {value}"));

        return $"""
        TASK {task.Id}: {task.Title}

        GOAL:
        {task.Prompt}

        READ FILES:
        {Lines(task.ReadFiles)}

        ALLOWED WRITE FILES:
        {Lines(task.WriteFiles)}

        ACCEPTANCE CHECKS:
        {Lines(task.AcceptanceChecks)}

        EXECUTION CAPABILITIES:
        {EnvironmentCapabilityService.FormatForPrompt(capabilities)}

        COMMON ENVELOPE RULES:
        - Set tasks to an empty array.
        - Set finalChecks to an empty array.
        - outcome, summary, checks, blocker and contextReuse carry the actual Worker result.

        RULES:
        - You execute only in the AGENT ENVIRONMENT.
        - Do not attempt a probed tool marked UNAVAILABLE in the agent environment, even if an acceptance check mentions it.
        - Host-only checks are performed separately by LoopGolem. Do not return blocked solely because a host-only check cannot run in your environment.
        - Do not infer that an unprobed tool is unavailable.
        - This is one microtask. Do not broaden scope.
        - Read the listed files first.
        - Do not modify any file outside ALLOWED WRITE FILES.
        - Do not commit, push, create branches, or rewrite Git history.
        - Network access is disabled.
        - Run acceptance checks when practical.
        - Return "blocked" if blocked.
        - Return "changed" only if a permitted file actually changed.
        - Return "already_satisfied" only if no edit was required.
        - contextReuse.recommended is only a hint to LoopGolem; it never authorizes broader work.
        - Set contextReuse.recommended=true only when your current repository understanding is likely to materially help an immediate follow-up microtask in the same area.
        - Set it false when this task was isolated, the useful context is exhausted, or carrying it forward could confuse later work.
        - Keep contextReuse.reason concise and technical.
        """;
    }

    private async Task<TaskExecutionResult?> EnsureCleanGitAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var status = await processRunner.RunAsync(
            "git",
            ["status", "--porcelain=v1", "--untracked-files=all"],
            workspace,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (status.ExitCode != 0)
        {
            return TaskExecutionResult.Failed(
                "Planner requires a Git repository.",
                GetProcessError(status));
        }

        return string.IsNullOrWhiteSpace(status.StandardOutput)
            ? null
            : TaskExecutionResult.Failed(
                "Planner requires a clean working tree.",
                "Commit, stash, or discard local changes before starting the autonomous mission.");
    }

    private static TaskExecutionResult? ValidateRuntime(
        CodexRuntimeStatus status)
    {
        if (!status.Available)
        {
            return TaskExecutionResult.Failed(
                "Codex runtime is unavailable.",
                status.Message);
        }

        if (!status.ChatGptAuthenticated)
        {
            return TaskExecutionResult.Failed(
                "Codex is not authenticated with ChatGPT.",
                status.Message);
        }

        return null;
    }

    private static string GetProcessError(ProcessRunResult process) =>
        string.IsNullOrWhiteSpace(process.StandardError)
            ? process.StandardOutput.Trim()
            : process.StandardError.Trim();

    private const string RecoverySchema =
        """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "tasks": {
              "type": "array",
              "minItems": 1,
              "maxItems": 12,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "title": { "type": "string" },
                  "executor": {
                    "type": "string",
                    "enum": ["luna_low"]
                  },
                  "prompt": { "type": "string" },
                  "readFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "writeFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "acceptanceChecks": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "dependsOn": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "rerunAfterRepair": { "type": "boolean" },
                  "deterministic": {
                    "type": "object",
                    "properties": {
                      "kind": {
                        "type": "string",
                        "enum": ["none"]
                      },
                      "path": { "type": "string" },
                      "content": { "type": "string" },
                      "sourcePath": { "type": "string" },
                      "destinationPath": { "type": "string" },
                      "executable": { "type": "string" },
                      "arguments": {
                        "type": "array",
                        "items": { "type": "string" }
                      },
                      "workingDirectory": { "type": "string" },
                      "timeoutSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 900
                      }
                    },
                    "required": [
                      "kind", "path", "content", "sourcePath",
                      "destinationPath", "executable", "arguments",
                      "workingDirectory", "timeoutSeconds"
                    ],
                    "additionalProperties": false
                  }
                },
                "required": [
                  "id", "title", "executor", "prompt", "readFiles",
                  "writeFiles", "acceptanceChecks", "dependsOn",
                  "rerunAfterRepair", "deterministic"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "tasks"],
          "additionalProperties": false
        }
        """;

    private const string ValidatorSchema =
        """
        {
          "type": "object",
          "properties": {
            "status": {
              "type": "string",
              "enum": ["ok", "not_ok"]
            },
            "summary": { "type": "string" },
            "tasks": {
              "type": "array",
              "maxItems": 100,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "title": { "type": "string" },
                  "executor": {
                    "type": "string",
                    "enum": ["deterministic", "luna_low"]
                  },
                  "prompt": { "type": "string" },
                  "readFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "writeFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "acceptanceChecks": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "dependsOn": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "rerunAfterRepair": { "type": "boolean" },
                  "deterministic": {
                    "type": "object",
                    "properties": {
                      "kind": {
                        "type": "string",
                        "enum": ["none", "write_file", "create_directory", "rename_path", "run_command"]
                      },
                      "path": { "type": "string" },
                      "content": { "type": "string" },
                      "sourcePath": { "type": "string" },
                      "destinationPath": { "type": "string" },
                      "executable": { "type": "string" },
                      "arguments": {
                        "type": "array",
                        "items": { "type": "string" }
                      },
                      "workingDirectory": { "type": "string" },
                      "timeoutSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 900
                      }
                    },
                    "required": [
                      "kind", "path", "content", "sourcePath",
                      "destinationPath", "executable", "arguments",
                      "workingDirectory", "timeoutSeconds"
                    ],
                    "additionalProperties": false
                  }
                },
                "required": [
                  "id", "title", "executor", "prompt", "readFiles",
                  "writeFiles", "acceptanceChecks", "dependsOn",
                  "rerunAfterRepair", "deterministic"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["status", "summary", "tasks"],
          "additionalProperties": false
        }
        """;

    private const string PlannerWorkerSchema =
        """
        {
          "type": "object",
          "properties": {
            "outcome": {
              "type": "string",
              "enum": ["plan", "changed", "already_satisfied", "blocked"]
            },
            "summary": { "type": "string" },
            "tasks": {
              "type": "array",
              "maxItems": 100,
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "title": { "type": "string" },
                  "executor": {
                    "type": "string",
                    "enum": ["deterministic", "luna_low"]
                  },
                  "prompt": { "type": "string" },
                  "readFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "writeFiles": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "acceptanceChecks": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "dependsOn": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "rerunAfterRepair": { "type": "boolean" },
                  "deterministic": {
                    "type": "object",
                    "properties": {
                      "kind": {
                        "type": "string",
                        "enum": ["none", "write_file", "create_directory", "rename_path", "run_command"]
                      },
                      "path": { "type": "string" },
                      "content": { "type": "string" },
                      "sourcePath": { "type": "string" },
                      "destinationPath": { "type": "string" },
                      "executable": { "type": "string" },
                      "arguments": {
                        "type": "array",
                        "items": { "type": "string" }
                      },
                      "workingDirectory": { "type": "string" },
                      "timeoutSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 900
                      }
                    },
                    "required": [
                      "kind", "path", "content", "sourcePath",
                      "destinationPath", "executable", "arguments",
                      "workingDirectory", "timeoutSeconds"
                    ],
                    "additionalProperties": false
                  }
                },
                "required": [
                  "id", "title", "executor", "prompt", "readFiles",
                  "writeFiles", "acceptanceChecks", "dependsOn",
                  "rerunAfterRepair", "deterministic"
                ],
                "additionalProperties": false
              }
            },
            "finalChecks": {
              "type": "array",
              "items": { "type": "string" }
            },
            "checks": {
              "type": "array",
              "items": { "type": "string" }
            },
            "blocker": { "type": "string" },
            "contextReuse": {
              "type": "object",
              "properties": {
                "recommended": { "type": "boolean" },
                "reason": { "type": "string" }
              },
              "required": ["recommended", "reason"],
              "additionalProperties": false
            }
          },
          "required": [
            "outcome", "summary", "tasks", "finalChecks",
            "checks", "blocker", "contextReuse"
          ],
          "additionalProperties": false
        }
        """;
}
