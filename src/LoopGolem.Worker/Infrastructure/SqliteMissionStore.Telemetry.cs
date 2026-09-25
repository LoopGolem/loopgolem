using System.Globalization;
using System.Text.Json;
using LoopGolem.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LoopGolem.Worker.Infrastructure;

public sealed partial class SqliteMissionStore
{
    private static async Task EnsureTelemetrySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mission_task_attempts (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                attempt_number INTEGER NOT NULL,
                outcome TEXT NOT NULL,
                failure_kind TEXT NOT NULL DEFAULT 'None',
                summary TEXT NULL,
                error TEXT NULL,
                evidence_json TEXT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                FOREIGN KEY (task_id) REFERENCES mission_tasks(id) ON DELETE CASCADE,
                UNIQUE (task_id, attempt_number)
            );

            CREATE TABLE IF NOT EXISTS agent_sessions (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL,
                role TEXT NOT NULL,
                model TEXT NOT NULL,
                reasoning_effort TEXT NOT NULL,
                provider_thread_id TEXT NULL,
                status TEXT NOT NULL,
                lease_owner_task_id TEXT NULL,
                turn_count INTEGER NOT NULL DEFAULT 0,
                microtask_count INTEGER NOT NULL DEFAULT 0,
                termination_reason TEXT NULL,
                created_utc TEXT NOT NULL,
                last_used_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS agent_turns (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL,
                task_id TEXT NULL,
                session_id TEXT NOT NULL,
                purpose TEXT NOT NULL,
                model TEXT NOT NULL,
                reasoning_effort TEXT NOT NULL,
                turn_number INTEGER NOT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                duration_milliseconds INTEGER NULL,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_output_tokens INTEGER NOT NULL DEFAULT 0,
                total_tokens INTEGER NOT NULL DEFAULT 0,
                context_reuse_recommended INTEGER NULL,
                context_reuse_reason TEXT NULL,
                FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                FOREIGN KEY (task_id) REFERENCES mission_tasks(id) ON DELETE SET NULL,
                FOREIGN KEY (session_id) REFERENCES agent_sessions(id) ON DELETE CASCADE,
                UNIQUE (session_id, turn_number)
            );

            CREATE TABLE IF NOT EXISTS recovery_cycles (
                id TEXT PRIMARY KEY,
                mission_id TEXT NOT NULL,
                failed_task_id TEXT NOT NULL,
                cycle_number INTEGER NOT NULL,
                status TEXT NOT NULL,
                failure_attempt_id TEXT NULL,
                recovery_turn_id TEXT NULL,
                repair_task_ids_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                FOREIGN KEY (failed_task_id) REFERENCES mission_tasks(id) ON DELETE CASCADE,
                FOREIGN KEY (failure_attempt_id) REFERENCES mission_task_attempts(id) ON DELETE SET NULL,
                FOREIGN KEY (recovery_turn_id) REFERENCES agent_turns(id) ON DELETE SET NULL,
                UNIQUE (failed_task_id, cycle_number)
            );

            CREATE INDEX IF NOT EXISTS ix_task_attempts_mission_id
                ON mission_task_attempts(mission_id);
            CREATE INDEX IF NOT EXISTS ix_agent_sessions_mission_id
                ON agent_sessions(mission_id);
            CREATE INDEX IF NOT EXISTS ix_agent_sessions_provider_thread_id
                ON agent_sessions(provider_thread_id);
            CREATE INDEX IF NOT EXISTS ix_agent_turns_mission_id
                ON agent_turns(mission_id);
            CREATE INDEX IF NOT EXISTS ix_agent_turns_session_id
                ON agent_turns(session_id);
            CREATE INDEX IF NOT EXISTS ix_recovery_cycles_mission_id
                ON recovery_cycles(mission_id);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnAsync(
            connection,
            "mission_task_attempts",
            "failure_kind",
            "TEXT NOT NULL DEFAULT 'None'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "agent_turns",
            "context_reuse_recommended",
            "INTEGER NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "agent_turns",
            "context_reuse_reason",
            "TEXT NULL",
            cancellationToken);
    }

    public async Task UpsertTaskAttemptAsync(
        MissionTaskAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mission_task_attempts (
                id, mission_id, task_id, attempt_number, outcome, failure_kind, summary, error,
                evidence_json, started_utc, completed_utc)
            VALUES (
                $id, $missionId, $taskId, $attemptNumber, $outcome, $failureKind, $summary, $error,
                $evidenceJson, $startedUtc, $completedUtc)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                task_id = excluded.task_id,
                attempt_number = excluded.attempt_number,
                outcome = excluded.outcome,
                failure_kind = excluded.failure_kind,
                summary = excluded.summary,
                error = excluded.error,
                evidence_json = excluded.evidence_json,
                started_utc = excluded.started_utc,
                completed_utc = excluded.completed_utc;
            """;

        command.Parameters.AddWithValue("$id", attempt.Id);
        command.Parameters.AddWithValue("$missionId", attempt.MissionId);
        command.Parameters.AddWithValue("$taskId", attempt.TaskId);
        command.Parameters.AddWithValue("$attemptNumber", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$outcome", attempt.Outcome.ToString());
        command.Parameters.AddWithValue("$failureKind", attempt.FailureKind.ToString());
        command.Parameters.AddWithValue("$summary", (object?)attempt.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)attempt.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidenceJson", (object?)attempt.EvidenceJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedUtc", FormatTimestamp(attempt.StartedAtUtc));
        command.Parameters.AddWithValue(
            "$completedUtc",
            attempt.CompletedAtUtc is { } completed
                ? FormatTimestamp(completed)
                : DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MissionTaskAttempt>> ListTaskAttemptsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_id, task_id, attempt_number, outcome, failure_kind, summary, error,
                   evidence_json, started_utc, completed_utc
            FROM mission_task_attempts
            WHERE mission_id = $missionId
            ORDER BY started_utc, attempt_number;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var results = new List<MissionTaskAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new MissionTaskAttempt(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    Enum.Parse<MissionTaskAttemptOutcome>(
                        reader.GetString(4)),
                    reader.IsDBNull(6)
                        ? null
                        : reader.GetString(6),
                    reader.IsDBNull(7)
                        ? null
                        : reader.GetString(7),
                    reader.IsDBNull(8)
                        ? null
                        : reader.GetString(8),
                    ParseTimestamp(
                        reader.GetString(9)),
                    reader.IsDBNull(10)
                        ? null
                        : ParseTimestamp(
                            reader.GetString(10)))
                {
                    FailureKind =
                        Enum.Parse<TaskFailureKind>(
                            reader.GetString(5))
                });
        }

        return results;
    }

    public async Task UpsertAgentSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_sessions (
                id, mission_id, role, model, reasoning_effort, provider_thread_id,
                status, lease_owner_task_id, turn_count, microtask_count,
                termination_reason, created_utc, last_used_utc, updated_utc)
            VALUES (
                $id, $missionId, $role, $model, $reasoningEffort, $providerThreadId,
                $status, $leaseOwnerTaskId, $turnCount, $microtaskCount,
                $terminationReason, $createdUtc, $lastUsedUtc, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                role = excluded.role,
                model = excluded.model,
                reasoning_effort = excluded.reasoning_effort,
                provider_thread_id = excluded.provider_thread_id,
                status = excluded.status,
                lease_owner_task_id = excluded.lease_owner_task_id,
                turn_count = excluded.turn_count,
                microtask_count = excluded.microtask_count,
                termination_reason = excluded.termination_reason,
                created_utc = excluded.created_utc,
                last_used_utc = excluded.last_used_utc,
                updated_utc = excluded.updated_utc;
            """;

        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$missionId", session.MissionId);
        command.Parameters.AddWithValue("$role", session.Role.ToString());
        command.Parameters.AddWithValue("$model", session.Model);
        command.Parameters.AddWithValue("$reasoningEffort", session.ReasoningEffort);
        command.Parameters.AddWithValue("$providerThreadId", (object?)session.ProviderThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$leaseOwnerTaskId", (object?)session.LeaseOwnerTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turnCount", session.TurnCount);
        command.Parameters.AddWithValue("$microtaskCount", session.MicrotaskCount);
        command.Parameters.AddWithValue("$terminationReason", (object?)session.TerminationReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(session.CreatedAtUtc));
        command.Parameters.AddWithValue("$lastUsedUtc", FormatTimestamp(session.LastUsedAtUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(session.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentSession>> ListAgentSessionsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_id, role, model, reasoning_effort, provider_thread_id,
                   status, lease_owner_task_id, turn_count, microtask_count,
                   termination_reason, created_utc, last_used_utc, updated_utc
            FROM agent_sessions
            WHERE mission_id = $missionId
            ORDER BY created_utc;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var results = new List<AgentSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AgentSession(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<AgentSessionRole>(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Enum.Parse<AgentSessionStatus>(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                ParseTimestamp(reader.GetString(11)),
                ParseTimestamp(reader.GetString(12)),
                ParseTimestamp(reader.GetString(13))));
        }

        return results;
    }

    public async Task UpsertAgentTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_turns (
                id, mission_id, task_id, session_id, purpose, model, reasoning_effort,
                turn_number, started_utc, completed_utc, duration_milliseconds,
                input_tokens, cached_input_tokens, cache_write_input_tokens,
                output_tokens, reasoning_output_tokens, total_tokens,
                context_reuse_recommended, context_reuse_reason)
            VALUES (
                $id, $missionId, $taskId, $sessionId, $purpose, $model, $reasoningEffort,
                $turnNumber, $startedUtc, $completedUtc, $durationMilliseconds,
                $inputTokens, $cachedInputTokens, $cacheWriteInputTokens,
                $outputTokens, $reasoningOutputTokens, $totalTokens,
                $contextReuseRecommended, $contextReuseReason)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                task_id = excluded.task_id,
                session_id = excluded.session_id,
                purpose = excluded.purpose,
                model = excluded.model,
                reasoning_effort = excluded.reasoning_effort,
                turn_number = excluded.turn_number,
                started_utc = excluded.started_utc,
                completed_utc = excluded.completed_utc,
                duration_milliseconds = excluded.duration_milliseconds,
                input_tokens = excluded.input_tokens,
                cached_input_tokens = excluded.cached_input_tokens,
                cache_write_input_tokens = excluded.cache_write_input_tokens,
                output_tokens = excluded.output_tokens,
                reasoning_output_tokens = excluded.reasoning_output_tokens,
                total_tokens = excluded.total_tokens,
                context_reuse_recommended = excluded.context_reuse_recommended,
                context_reuse_reason = excluded.context_reuse_reason;
            """;

        command.Parameters.AddWithValue("$id", turn.Id);
        command.Parameters.AddWithValue("$missionId", turn.MissionId);
        command.Parameters.AddWithValue("$taskId", (object?)turn.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", turn.SessionId);
        command.Parameters.AddWithValue("$purpose", turn.Purpose.ToString());
        command.Parameters.AddWithValue("$model", turn.Model);
        command.Parameters.AddWithValue("$reasoningEffort", turn.ReasoningEffort);
        command.Parameters.AddWithValue("$turnNumber", turn.TurnNumber);
        command.Parameters.AddWithValue("$startedUtc", FormatTimestamp(turn.StartedAtUtc));
        command.Parameters.AddWithValue(
            "$completedUtc",
            turn.CompletedAtUtc is { } completed
                ? FormatTimestamp(completed)
                : DBNull.Value);
        command.Parameters.AddWithValue("$durationMilliseconds", (object?)turn.DurationMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$inputTokens", turn.InputTokens);
        command.Parameters.AddWithValue("$cachedInputTokens", turn.CachedInputTokens);
        command.Parameters.AddWithValue("$cacheWriteInputTokens", turn.CacheWriteInputTokens);
        command.Parameters.AddWithValue("$outputTokens", turn.OutputTokens);
        command.Parameters.AddWithValue("$reasoningOutputTokens", turn.ReasoningOutputTokens);
        command.Parameters.AddWithValue("$totalTokens", turn.TotalTokens);
        command.Parameters.AddWithValue(
            "$contextReuseRecommended",
            turn.ContextReuseRecommended is { } recommended
                ? recommended ? 1 : 0
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$contextReuseReason",
            (object?)turn.ContextReuseReason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentTurn>> ListAgentTurnsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_id, task_id, session_id, purpose, model, reasoning_effort,
                   turn_number, started_utc, completed_utc, duration_milliseconds,
                   input_tokens, cached_input_tokens, cache_write_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens,
                   context_reuse_recommended, context_reuse_reason
            FROM agent_turns
            WHERE mission_id = $missionId
            ORDER BY started_utc, turn_number;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var results = new List<AgentTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new AgentTurn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    Enum.Parse<AgentTurnPurpose>(reader.GetString(4)),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt32(7),
                    ParseTimestamp(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.GetInt64(11),
                    reader.GetInt64(12),
                    reader.GetInt64(13),
                    reader.GetInt64(14),
                    reader.GetInt64(15),
                    reader.GetInt64(16))
                {
                    ContextReuseRecommended =
                        reader.IsDBNull(17)
                            ? null
                            : reader.GetInt64(17) != 0,
                    ContextReuseReason =
                        reader.IsDBNull(18)
                            ? null
                            : reader.GetString(18)
                });
        }

        return results;
    }

    public async Task UpsertRecoveryCycleAsync(
        RecoveryCycle cycle,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recovery_cycles (
                id, mission_id, failed_task_id, cycle_number, status,
                failure_attempt_id, recovery_turn_id, repair_task_ids_json,
                created_utc, updated_utc)
            VALUES (
                $id, $missionId, $failedTaskId, $cycleNumber, $status,
                $failureAttemptId, $recoveryTurnId, $repairTaskIdsJson,
                $createdUtc, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                failed_task_id = excluded.failed_task_id,
                cycle_number = excluded.cycle_number,
                status = excluded.status,
                failure_attempt_id = excluded.failure_attempt_id,
                recovery_turn_id = excluded.recovery_turn_id,
                repair_task_ids_json = excluded.repair_task_ids_json,
                created_utc = excluded.created_utc,
                updated_utc = excluded.updated_utc;
            """;

        command.Parameters.AddWithValue("$id", cycle.Id);
        command.Parameters.AddWithValue("$missionId", cycle.MissionId);
        command.Parameters.AddWithValue("$failedTaskId", cycle.FailedTaskId);
        command.Parameters.AddWithValue("$cycleNumber", cycle.CycleNumber);
        command.Parameters.AddWithValue("$status", cycle.Status.ToString());
        command.Parameters.AddWithValue("$failureAttemptId", (object?)cycle.FailureAttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("$recoveryTurnId", (object?)cycle.RecoveryTurnId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$repairTaskIdsJson",
            JsonSerializer.Serialize(cycle.RepairTaskIds));
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(cycle.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(cycle.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecoveryCycle>> ListRecoveryCyclesAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_id, failed_task_id, cycle_number, status,
                   failure_attempt_id, recovery_turn_id, repair_task_ids_json,
                   created_utc, updated_utc
            FROM recovery_cycles
            WHERE mission_id = $missionId
            ORDER BY cycle_number, created_utc;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var results = new List<RecoveryCycle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RecoveryCycle(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                Enum.Parse<RecoveryCycleStatus>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [],
                ParseTimestamp(reader.GetString(8)),
                ParseTimestamp(reader.GetString(9))));
        }

        return results;
    }
}
