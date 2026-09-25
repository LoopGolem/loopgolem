using System.Globalization;
using System.Text.Json;
using LoopGolem.Core.Domain;
using LoopGolem.Orchestrator;
using Microsoft.Data.Sqlite;
using DomainTaskStatus = LoopGolem.Core.Domain.TaskStatus;

namespace LoopGolem.Worker.Infrastructure;

public sealed class SqliteMissionStore(string databasePath) : IMissionStore
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("Database directory could not be resolved.");

        Directory.CreateDirectory(directory);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS missions (
                    id TEXT PRIMARY KEY,
                    goal TEXT NOT NULL,
                    workspace_path TEXT NOT NULL,
                    execution_mode TEXT NOT NULL DEFAULT 'ValidateOnly',
                    policy_json TEXT NULL,
                    status TEXT NOT NULL,
                    result TEXT NULL,
                    error TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS mission_tasks (
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

                CREATE INDEX IF NOT EXISTS ix_mission_tasks_mission_id
                    ON mission_tasks(mission_id);

                CREATE TABLE IF NOT EXISTS mission_task_attempts (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    task_id TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL,
                    outcome TEXT NOT NULL,
                    failure_kind TEXT NOT NULL,
                    summary TEXT NULL,
                    evidence_json TEXT NULL,
                    started_utc TEXT NOT NULL,
                    completed_utc TEXT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                    FOREIGN KEY (task_id) REFERENCES mission_tasks(id) ON DELETE CASCADE,
                    UNIQUE (task_id, attempt_number)
                );

                CREATE INDEX IF NOT EXISTS ix_mission_task_attempts_mission_id
                    ON mission_task_attempts(mission_id);

                CREATE TABLE IF NOT EXISTS agent_sessions (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    thread_id TEXT NULL,
                    model TEXT NOT NULL,
                    reasoning_effort TEXT NOT NULL,
                    status TEXT NOT NULL,
                    lease_task_id TEXT NULL,
                    turn_count INTEGER NOT NULL DEFAULT 0,
                    microtask_count INTEGER NOT NULL DEFAULT 0,
                    created_utc TEXT NOT NULL,
                    last_used_utc TEXT NOT NULL,
                    closed_utc TEXT NULL,
                    termination_reason TEXT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                    FOREIGN KEY (lease_task_id) REFERENCES mission_tasks(id) ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS ix_agent_sessions_mission_id
                    ON agent_sessions(mission_id);

                CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_sessions_thread_id
                    ON agent_sessions(thread_id)
                    WHERE thread_id IS NOT NULL;

                CREATE TABLE IF NOT EXISTS agent_turns (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    task_id TEXT NULL,
                    purpose TEXT NOT NULL,
                    turn_number INTEGER NOT NULL,
                    model TEXT NOT NULL,
                    reasoning_effort TEXT NOT NULL,
                    input_tokens INTEGER NULL,
                    cached_input_tokens INTEGER NULL,
                    cache_write_input_tokens INTEGER NULL,
                    output_tokens INTEGER NULL,
                    reasoning_output_tokens INTEGER NULL,
                    total_tokens INTEGER NULL,
                    started_utc TEXT NOT NULL,
                    completed_utc TEXT NULL,
                    duration_ms INTEGER NULL,
                    success INTEGER NULL,
                    error TEXT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                    FOREIGN KEY (session_id) REFERENCES agent_sessions(id) ON DELETE CASCADE,
                    FOREIGN KEY (task_id) REFERENCES mission_tasks(id) ON DELETE SET NULL,
                    UNIQUE (session_id, turn_number)
                );

                CREATE INDEX IF NOT EXISTS ix_agent_turns_mission_id
                    ON agent_turns(mission_id);

                CREATE TABLE IF NOT EXISTS recovery_episodes (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    failed_task_id TEXT NOT NULL,
                    cycle INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    failed_attempt_id TEXT NULL,
                    recovery_turn_id TEXT NULL,
                    repair_task_ids_json TEXT NOT NULL,
                    failure_evidence_json TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE,
                    FOREIGN KEY (failed_task_id) REFERENCES mission_tasks(id) ON DELETE CASCADE,
                    FOREIGN KEY (failed_attempt_id) REFERENCES mission_task_attempts(id) ON DELETE SET NULL,
                    FOREIGN KEY (recovery_turn_id) REFERENCES agent_turns(id) ON DELETE SET NULL,
                    UNIQUE (failed_task_id, cycle)
                );

                CREATE INDEX IF NOT EXISTS ix_recovery_episodes_mission_id
                    ON recovery_episodes(mission_id);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureColumnAsync(
            connection,
            "missions",
            "execution_mode",
            "TEXT NOT NULL DEFAULT 'ValidateOnly'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "missions",
            "policy_json",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "mission_tasks",
            "kind",
            "TEXT NOT NULL DEFAULT 'InspectWorkspace'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "mission_tasks",
            "definition_json",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "mission_tasks",
            "result_details",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "mission_tasks",
            "execution_context",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "mission_tasks",
            "execution_attempt_count",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "mission_tasks",
            "token_usage_json",
            "TEXT NULL",
            cancellationToken);

        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_mission_tasks_mission_sequence
                ON mission_tasks(mission_id, sequence);
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateAsync(
        MissionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var missionCommand = connection.CreateCommand())
        {
            missionCommand.Transaction = (SqliteTransaction)transaction;
            missionCommand.CommandText = """
                INSERT INTO missions (
                    id, goal, workspace_path, execution_mode, policy_json, status, result, error, created_utc, updated_utc)
                VALUES (
                    $id, $goal, $workspacePath, $executionMode, $policyJson, $status, $result, $error, $createdUtc, $updatedUtc);
                """;

            AddMissionParameters(missionCommand, snapshot.Mission);
            await missionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var task in snapshot.Tasks.OrderBy(task => task.Sequence))
        {
            await InsertTaskAsync(
                connection,
                (SqliteTransaction)transaction,
                task,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MissionSnapshot?> GetAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadSnapshotAsync(connection, missionId, cancellationToken);
    }

    public async Task<IReadOnlyList<MissionSnapshot>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM missions
            WHERE status IN ('Created', 'Planning', 'Running')
            ORDER BY created_utc;
            """;

        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var snapshots = new List<MissionSnapshot>(ids.Count);
        foreach (var id in ids)
        {
            var snapshot = await ReadSnapshotAsync(connection, id, cancellationToken);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    public async Task UpdateAsync(
        MissionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var missionCommand = connection.CreateCommand())
        {
            missionCommand.Transaction = (SqliteTransaction)transaction;
            missionCommand.CommandText = """
                UPDATE missions SET
                    goal = $goal,
                    workspace_path = $workspacePath,
                    execution_mode = $executionMode,
                    policy_json = $policyJson,
                    status = $status,
                    result = $result,
                    error = $error,
                    created_utc = $createdUtc,
                    updated_utc = $updatedUtc
                WHERE id = $id;
                """;

            AddMissionParameters(missionCommand, snapshot.Mission);
            await missionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var task in snapshot.Tasks.OrderBy(task => task.Sequence))
        {
            await UpsertTaskAsync(
                connection,
                (SqliteTransaction)transaction,
                task,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }


    public async Task SaveTaskAttemptAsync(
        MissionTaskAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mission_task_attempts (
                id, mission_id, task_id, attempt_number, outcome, failure_kind,
                summary, evidence_json, started_utc, completed_utc)
            VALUES (
                $id, $missionId, $taskId, $attemptNumber, $outcome, $failureKind,
                $summary, $evidenceJson, $startedUtc, $completedUtc)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                task_id = excluded.task_id,
                attempt_number = excluded.attempt_number,
                outcome = excluded.outcome,
                failure_kind = excluded.failure_kind,
                summary = excluded.summary,
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
            SELECT id, mission_id, task_id, attempt_number, outcome, failure_kind,
                   summary, evidence_json, started_utc, completed_utc
            FROM mission_task_attempts
            WHERE mission_id = $missionId
            ORDER BY task_id, attempt_number;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var attempts = new List<MissionTaskAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(new MissionTaskAttempt(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                Enum.Parse<TaskAttemptOutcome>(reader.GetString(4)),
                Enum.Parse<TaskFailureKind>(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                ParseTimestamp(reader.GetString(8)),
                reader.IsDBNull(9)
                    ? null
                    : ParseTimestamp(reader.GetString(9))));
        }

        return attempts;
    }

    public async Task SaveAgentSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_sessions (
                id, mission_id, role, provider, thread_id, model, reasoning_effort,
                status, lease_task_id, turn_count, microtask_count, created_utc,
                last_used_utc, closed_utc, termination_reason)
            VALUES (
                $id, $missionId, $role, $provider, $threadId, $model, $reasoningEffort,
                $status, $leaseTaskId, $turnCount, $microtaskCount, $createdUtc,
                $lastUsedUtc, $closedUtc, $terminationReason)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                role = excluded.role,
                provider = excluded.provider,
                thread_id = excluded.thread_id,
                model = excluded.model,
                reasoning_effort = excluded.reasoning_effort,
                status = excluded.status,
                lease_task_id = excluded.lease_task_id,
                turn_count = excluded.turn_count,
                microtask_count = excluded.microtask_count,
                created_utc = excluded.created_utc,
                last_used_utc = excluded.last_used_utc,
                closed_utc = excluded.closed_utc,
                termination_reason = excluded.termination_reason;
            """;

        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$missionId", session.MissionId);
        command.Parameters.AddWithValue("$role", session.Role.ToString());
        command.Parameters.AddWithValue("$provider", session.Provider);
        command.Parameters.AddWithValue("$threadId", (object?)session.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", session.Model);
        command.Parameters.AddWithValue("$reasoningEffort", session.ReasoningEffort);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$leaseTaskId", (object?)session.LeaseTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turnCount", session.TurnCount);
        command.Parameters.AddWithValue("$microtaskCount", session.MicrotaskCount);
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(session.CreatedAtUtc));
        command.Parameters.AddWithValue("$lastUsedUtc", FormatTimestamp(session.LastUsedAtUtc));
        command.Parameters.AddWithValue(
            "$closedUtc",
            session.ClosedAtUtc is { } closed
                ? FormatTimestamp(closed)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$terminationReason",
            (object?)session.TerminationReason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentSession>> ListAgentSessionsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_id, role, provider, thread_id, model, reasoning_effort,
                   status, lease_task_id, turn_count, microtask_count, created_utc,
                   last_used_utc, closed_utc, termination_reason
            FROM agent_sessions
            WHERE mission_id = $missionId
            ORDER BY created_utc, id;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var sessions = new List<AgentSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new AgentSession(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<AgentSessionRole>(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                Enum.Parse<AgentSessionStatus>(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                ParseTimestamp(reader.GetString(11)),
                ParseTimestamp(reader.GetString(12)),
                reader.IsDBNull(13)
                    ? null
                    : ParseTimestamp(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return sessions;
    }

    public async Task SaveAgentTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_turns (
                id, mission_id, session_id, task_id, purpose, turn_number, model,
                reasoning_effort, input_tokens, cached_input_tokens,
                cache_write_input_tokens, output_tokens, reasoning_output_tokens,
                total_tokens, started_utc, completed_utc, duration_ms, success, error)
            VALUES (
                $id, $missionId, $sessionId, $taskId, $purpose, $turnNumber, $model,
                $reasoningEffort, $inputTokens, $cachedInputTokens,
                $cacheWriteInputTokens, $outputTokens, $reasoningOutputTokens,
                $totalTokens, $startedUtc, $completedUtc, $durationMs, $success, $error)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                session_id = excluded.session_id,
                task_id = excluded.task_id,
                purpose = excluded.purpose,
                turn_number = excluded.turn_number,
                model = excluded.model,
                reasoning_effort = excluded.reasoning_effort,
                input_tokens = excluded.input_tokens,
                cached_input_tokens = excluded.cached_input_tokens,
                cache_write_input_tokens = excluded.cache_write_input_tokens,
                output_tokens = excluded.output_tokens,
                reasoning_output_tokens = excluded.reasoning_output_tokens,
                total_tokens = excluded.total_tokens,
                started_utc = excluded.started_utc,
                completed_utc = excluded.completed_utc,
                duration_ms = excluded.duration_ms,
                success = excluded.success,
                error = excluded.error;
            """;

        command.Parameters.AddWithValue("$id", turn.Id);
        command.Parameters.AddWithValue("$missionId", turn.MissionId);
        command.Parameters.AddWithValue("$sessionId", turn.SessionId);
        command.Parameters.AddWithValue("$taskId", (object?)turn.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$purpose", turn.Purpose.ToString());
        command.Parameters.AddWithValue("$turnNumber", turn.TurnNumber);
        command.Parameters.AddWithValue("$model", turn.Model);
        command.Parameters.AddWithValue("$reasoningEffort", turn.ReasoningEffort);
        AddTokenUsageParameters(command, turn.TokenUsage);
        command.Parameters.AddWithValue("$startedUtc", FormatTimestamp(turn.StartedAtUtc));
        command.Parameters.AddWithValue(
            "$completedUtc",
            turn.CompletedAtUtc is { } completed
                ? FormatTimestamp(completed)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$durationMs",
            turn.DurationMilliseconds is { } duration
                ? duration
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$success",
            turn.Success is { } success
                ? success ? 1 : 0
                : DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)turn.Error ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentTurn>> ListAgentTurnsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_id, session_id, task_id, purpose, turn_number, model,
                   reasoning_effort, input_tokens, cached_input_tokens,
                   cache_write_input_tokens, output_tokens, reasoning_output_tokens,
                   total_tokens, started_utc, completed_utc, duration_ms, success, error
            FROM agent_turns
            WHERE mission_id = $missionId
            ORDER BY started_utc, id;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var turns = new List<AgentTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            TokenUsage? usage = null;
            if (!reader.IsDBNull(8))
            {
                usage = new TokenUsage(
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(11),
                    reader.GetInt64(12),
                    reader.GetInt64(13))
                {
                    CacheWriteInputTokens = reader.GetInt64(10)
                };
            }

            turns.Add(new AgentTurn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Enum.Parse<AgentTurnPurpose>(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                usage,
                ParseTimestamp(reader.GetString(14)),
                reader.IsDBNull(15)
                    ? null
                    : ParseTimestamp(reader.GetString(15)),
                reader.IsDBNull(16) ? null : reader.GetInt64(16),
                reader.IsDBNull(17) ? null : reader.GetInt64(17) != 0,
                reader.IsDBNull(18) ? null : reader.GetString(18)));
        }

        return turns;
    }

    public async Task SaveRecoveryEpisodeAsync(
        RecoveryEpisode episode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recovery_episodes (
                id, mission_id, failed_task_id, cycle, status, failed_attempt_id,
                recovery_turn_id, repair_task_ids_json, failure_evidence_json,
                created_utc, updated_utc)
            VALUES (
                $id, $missionId, $failedTaskId, $cycle, $status, $failedAttemptId,
                $recoveryTurnId, $repairTaskIdsJson, $failureEvidenceJson,
                $createdUtc, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET
                mission_id = excluded.mission_id,
                failed_task_id = excluded.failed_task_id,
                cycle = excluded.cycle,
                status = excluded.status,
                failed_attempt_id = excluded.failed_attempt_id,
                recovery_turn_id = excluded.recovery_turn_id,
                repair_task_ids_json = excluded.repair_task_ids_json,
                failure_evidence_json = excluded.failure_evidence_json,
                created_utc = excluded.created_utc,
                updated_utc = excluded.updated_utc;
            """;

        command.Parameters.AddWithValue("$id", episode.Id);
        command.Parameters.AddWithValue("$missionId", episode.MissionId);
        command.Parameters.AddWithValue("$failedTaskId", episode.FailedTaskId);
        command.Parameters.AddWithValue("$cycle", episode.Cycle);
        command.Parameters.AddWithValue("$status", episode.Status.ToString());
        command.Parameters.AddWithValue(
            "$failedAttemptId",
            (object?)episode.FailedAttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$recoveryTurnId",
            (object?)episode.RecoveryTurnId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$repairTaskIdsJson",
            JsonSerializer.Serialize(episode.RepairTaskIds));
        command.Parameters.AddWithValue(
            "$failureEvidenceJson",
            (object?)episode.FailureEvidenceJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(episode.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(episode.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecoveryEpisode>> ListRecoveryEpisodesAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_id, failed_task_id, cycle, status, failed_attempt_id,
                   recovery_turn_id, repair_task_ids_json, failure_evidence_json,
                   created_utc, updated_utc
            FROM recovery_episodes
            WHERE mission_id = $missionId
            ORDER BY failed_task_id, cycle;
            """;
        command.Parameters.AddWithValue("$missionId", missionId);

        var episodes = new List<RecoveryEpisode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            episodes.Add(new RecoveryEpisode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                Enum.Parse<RecoveryEpisodeStatus>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                JsonSerializer.Deserialize<IReadOnlyList<string>>(
                    reader.GetString(7)) ?? [],
                reader.IsDBNull(8) ? null : reader.GetString(8),
                ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10))));
        }

        return episodes;
    }

    private static void AddTokenUsageParameters(
        SqliteCommand command,
        TokenUsage? usage)
    {
        command.Parameters.AddWithValue(
            "$inputTokens",
            usage is null ? DBNull.Value : usage.InputTokens);
        command.Parameters.AddWithValue(
            "$cachedInputTokens",
            usage is null ? DBNull.Value : usage.CachedInputTokens);
        command.Parameters.AddWithValue(
            "$cacheWriteInputTokens",
            usage is null ? DBNull.Value : usage.CacheWriteInputTokens);
        command.Parameters.AddWithValue(
            "$outputTokens",
            usage is null ? DBNull.Value : usage.OutputTokens);
        command.Parameters.AddWithValue(
            "$reasoningOutputTokens",
            usage is null ? DBNull.Value : usage.ReasoningOutputTokens);
        command.Parameters.AddWithValue(
            "$totalTokens",
            usage is null ? DBNull.Value : usage.TotalTokens);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(
                connection,
                tableName,
                columnName,
                cancellationToken))
        {
            return;
        }

        await using var migration = connection.CreateCommand();
        migration.CommandText =
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await migration.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task InsertTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MissionTask task,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mission_tasks (
                id, mission_id, sequence, kind, title, definition_json, execution_context, execution_attempt_count, status,
                result, result_details, error, created_utc, updated_utc)
            VALUES (
                $id, $missionId, $sequence, $kind, $title, $definitionJson, $executionContext, $executionAttemptCount, $status,
                $result, $resultDetails, $error, $createdUtc, $updatedUtc);
            """;

        AddTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MissionTask task,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mission_tasks (
                id, mission_id, sequence, kind, title, definition_json, execution_context, execution_attempt_count, token_usage_json, status,
                result, result_details, error, created_utc, updated_utc)
            VALUES (
                $id, $missionId, $sequence, $kind, $title, $definitionJson, $executionContext, $executionAttemptCount, $tokenUsageJson, $status,
                $result, $resultDetails, $error, $createdUtc, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET
                sequence = excluded.sequence,
                kind = excluded.kind,
                title = excluded.title,
                definition_json = excluded.definition_json,
                execution_context = excluded.execution_context,
                execution_attempt_count = excluded.execution_attempt_count,
                token_usage_json = excluded.token_usage_json,
                status = excluded.status,
                result = excluded.result,
                result_details = excluded.result_details,
                error = excluded.error,
                created_utc = excluded.created_utc,
                updated_utc = excluded.updated_utc;
            """;

        AddTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MissionSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        string missionId,
        CancellationToken cancellationToken)
    {
        Mission? mission = null;

        await using (var missionCommand = connection.CreateCommand())
        {
            missionCommand.CommandText = """
                SELECT id, goal, workspace_path, execution_mode, policy_json, status, result, error, created_utc, updated_utc
                FROM missions
                WHERE id = $id;
                """;
            missionCommand.Parameters.AddWithValue("$id", missionId);

            await using var reader = await missionCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                mission = new Mission(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Enum.Parse<MissionExecutionMode>(reader.GetString(3)),
                    Enum.Parse<MissionStatus>(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    ParseTimestamp(reader.GetString(8)),
                    ParseTimestamp(reader.GetString(9)))
                {
                    Policy = reader.IsDBNull(4)
                        ? MissionExecutionPolicy.Default
                        : JsonSerializer.Deserialize<MissionExecutionPolicy>(
                              reader.GetString(4))
                          ?? MissionExecutionPolicy.Default
                };
            }
        }

        if (mission is null)
        {
            return null;
        }

        await using var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT id, mission_id, sequence, kind, title, definition_json, execution_context, status,
                   result, result_details, error, created_utc, updated_utc, execution_attempt_count, token_usage_json
            FROM mission_tasks
            WHERE mission_id = $missionId
            ORDER BY sequence;
            """;
        taskCommand.Parameters.AddWithValue("$missionId", missionId);

        var tasks = new List<MissionTask>();
        await using var taskReader = await taskCommand.ExecuteReaderAsync(cancellationToken);

        while (await taskReader.ReadAsync(cancellationToken))
        {
            tasks.Add(new MissionTask(
                taskReader.GetString(0),
                taskReader.GetString(1),
                taskReader.GetInt32(2),
                Enum.Parse<MissionTaskKind>(taskReader.GetString(3)),
                taskReader.GetString(4),
                taskReader.IsDBNull(5)
                    ? null
                    : JsonSerializer.Deserialize<PlannedTask>(taskReader.GetString(5)),
                Enum.Parse<DomainTaskStatus>(taskReader.GetString(7)),
                taskReader.IsDBNull(8) ? null : taskReader.GetString(8),
                taskReader.IsDBNull(9) ? null : taskReader.GetString(9),
                taskReader.IsDBNull(10) ? null : taskReader.GetString(10),
                ParseTimestamp(taskReader.GetString(11)),
                ParseTimestamp(taskReader.GetString(12)))
            {
                ExecutionContext =
                    taskReader.IsDBNull(6)
                        ? null
                        : taskReader.GetString(6),
                ExecutionAttemptCount = taskReader.GetInt32(13),
                TokenUsage = taskReader.IsDBNull(14)
                    ? null
                    : JsonSerializer.Deserialize<TokenUsage>(
                        taskReader.GetString(14))
            });
        }

        if (tasks.Count == 0)
        {
            throw new InvalidDataException($"Mission '{missionId}' has no tasks.");
        }

        return new MissionSnapshot(mission, tasks);
    }

    private static void AddMissionParameters(
        SqliteCommand command,
        Mission mission)
    {
        command.Parameters.AddWithValue("$id", mission.Id);
        command.Parameters.AddWithValue("$goal", mission.Goal);
        command.Parameters.AddWithValue("$workspacePath", mission.WorkspacePath);
        command.Parameters.AddWithValue("$executionMode", mission.ExecutionMode.ToString());
        command.Parameters.AddWithValue(
            "$policyJson",
            JsonSerializer.Serialize(mission.Policy));
        command.Parameters.AddWithValue("$status", mission.Status.ToString());
        command.Parameters.AddWithValue("$result", (object?)mission.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)mission.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(mission.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(mission.UpdatedAtUtc));
    }

    private static void AddTaskParameters(
        SqliteCommand command,
        MissionTask task)
    {
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$missionId", task.MissionId);
        command.Parameters.AddWithValue("$sequence", task.Sequence);
        command.Parameters.AddWithValue("$kind", task.Kind.ToString());
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue(
            "$definitionJson",
            task.Definition is null
                ? DBNull.Value
                : JsonSerializer.Serialize(task.Definition));
        command.Parameters.AddWithValue(
            "$executionContext",
            (object?)task.ExecutionContext ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionAttemptCount", task.ExecutionAttemptCount);
        command.Parameters.AddWithValue(
            "$tokenUsageJson",
            task.TokenUsage is null
                ? DBNull.Value
                : JsonSerializer.Serialize(task.TokenUsage));
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$result", (object?)task.Result ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$resultDetails",
            (object?)task.ResultDetails ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)task.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(task.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(task.UpdatedAtUtc));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
