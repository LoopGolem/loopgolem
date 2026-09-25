using System.Text.Json;
using LoopGolem.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LoopGolem.Worker.Infrastructure;

public sealed partial class SqliteMissionStore
{
    private static async Task EnsureCapabilitySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mission_capability_snapshots (
                mission_id TEXT PRIMARY KEY,
                snapshot_json TEXT NOT NULL,
                captured_utc TEXT NOT NULL,
                FOREIGN KEY (mission_id) REFERENCES missions(id) ON DELETE CASCADE
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MissionCapabilitySnapshot?>
        GetCapabilitySnapshotAsync(
            string missionId,
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_json
            FROM mission_capability_snapshots
            WHERE mission_id = $missionId;
            """;
        command.Parameters.AddWithValue(
            "$missionId",
            missionId);

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value is string json
            ? JsonSerializer.Deserialize<MissionCapabilitySnapshot>(
                json)
            : null;
    }

    public async Task UpsertCapabilitySnapshotAsync(
        MissionCapabilitySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mission_capability_snapshots (
                mission_id, snapshot_json, captured_utc)
            VALUES (
                $missionId, $snapshotJson, $capturedUtc)
            ON CONFLICT(mission_id) DO UPDATE SET
                snapshot_json = excluded.snapshot_json,
                captured_utc = excluded.captured_utc;
            """;

        command.Parameters.AddWithValue(
            "$missionId",
            snapshot.MissionId);
        command.Parameters.AddWithValue(
            "$snapshotJson",
            JsonSerializer.Serialize(snapshot));
        command.Parameters.AddWithValue(
            "$capturedUtc",
            FormatTimestamp(snapshot.CapturedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
