using System.Globalization;
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS missions (
                id TEXT PRIMARY KEY,
                goal TEXT NOT NULL,
                workspace_path TEXT NOT NULL,
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
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                result TEXT NULL,
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
                    id, goal, workspace_path, status, result, error, created_utc, updated_utc)
                VALUES (
                    $id, $goal, $workspacePath, $status, $result, $error, $createdUtc, $updatedUtc);
                """;

            AddMissionParameters(missionCommand, snapshot.Mission);
            await missionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.Transaction = (SqliteTransaction)transaction;
            taskCommand.CommandText = """
                INSERT INTO mission_tasks (
                    id, mission_id, sequence, title, status, result, error, created_utc, updated_utc)
                VALUES (
                    $id, $missionId, $sequence, $title, $status, $result, $error, $createdUtc, $updatedUtc);
                """;

            AddTaskParameters(taskCommand, snapshot.Task);
            await taskCommand.ExecuteNonQueryAsync(cancellationToken);
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

        await using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.Transaction = (SqliteTransaction)transaction;
            taskCommand.CommandText = """
                UPDATE mission_tasks SET
                    sequence = $sequence,
                    title = $title,
                    status = $status,
                    result = $result,
                    error = $error,
                    created_utc = $createdUtc,
                    updated_utc = $updatedUtc
                WHERE id = $id AND mission_id = $missionId;
                """;

            AddTaskParameters(taskCommand, snapshot.Task);
            await taskCommand.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<MissionSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        string missionId,
        CancellationToken cancellationToken)
    {
        Mission? mission = null;

        await using (var missionCommand = connection.CreateCommand())
        {
            missionCommand.CommandText = """
                SELECT id, goal, workspace_path, status, result, error, created_utc, updated_utc
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
                    Enum.Parse<MissionStatus>(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    ParseTimestamp(reader.GetString(6)),
                    ParseTimestamp(reader.GetString(7)));
            }
        }

        if (mission is null)
        {
            return null;
        }

        await using var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT id, mission_id, sequence, title, status, result, error, created_utc, updated_utc
            FROM mission_tasks
            WHERE mission_id = $missionId
            ORDER BY sequence
            LIMIT 1;
            """;
        taskCommand.Parameters.AddWithValue("$missionId", missionId);

        await using var taskReader = await taskCommand.ExecuteReaderAsync(cancellationToken);
        if (!await taskReader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException($"Mission '{missionId}' has no task.");
        }

        var task = new MissionTask(
            taskReader.GetString(0),
            taskReader.GetString(1),
            taskReader.GetInt32(2),
            taskReader.GetString(3),
            Enum.Parse<DomainTaskStatus>(taskReader.GetString(4)),
            taskReader.IsDBNull(5) ? null : taskReader.GetString(5),
            taskReader.IsDBNull(6) ? null : taskReader.GetString(6),
            ParseTimestamp(taskReader.GetString(7)),
            ParseTimestamp(taskReader.GetString(8)));

        return new MissionSnapshot(mission, task);
    }

    private static void AddMissionParameters(
        SqliteCommand command,
        Mission mission)
    {
        command.Parameters.AddWithValue("$id", mission.Id);
        command.Parameters.AddWithValue("$goal", mission.Goal);
        command.Parameters.AddWithValue("$workspacePath", mission.WorkspacePath);
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
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$result", (object?)task.Result ?? DBNull.Value);
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
