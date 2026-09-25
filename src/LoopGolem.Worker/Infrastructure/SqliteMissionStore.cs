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
                    id, goal, workspace_path, execution_mode, status, result, error, created_utc, updated_utc)
                VALUES (
                    $id, $goal, $workspacePath, $executionMode, $status, $result, $error, $createdUtc, $updatedUtc);
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
                id, mission_id, sequence, kind, title, definition_json, execution_context, status,
                result, result_details, error, created_utc, updated_utc)
            VALUES (
                $id, $missionId, $sequence, $kind, $title, $definitionJson, $executionContext, $status,
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
                id, mission_id, sequence, kind, title, definition_json, execution_context, status,
                result, result_details, error, created_utc, updated_utc)
            VALUES (
                $id, $missionId, $sequence, $kind, $title, $definitionJson, $executionContext, $status,
                $result, $resultDetails, $error, $createdUtc, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET
                sequence = excluded.sequence,
                kind = excluded.kind,
                title = excluded.title,
                definition_json = excluded.definition_json,
                execution_context = excluded.execution_context,
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
                SELECT id, goal, workspace_path, execution_mode, status, result, error, created_utc, updated_utc
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
                    Enum.Parse<MissionStatus>(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    ParseTimestamp(reader.GetString(7)),
                    ParseTimestamp(reader.GetString(8)));
            }
        }

        if (mission is null)
        {
            return null;
        }

        await using var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT id, mission_id, sequence, kind, title, definition_json, execution_context, status,
                   result, result_details, error, created_utc, updated_utc
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
                        : taskReader.GetString(6)
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
