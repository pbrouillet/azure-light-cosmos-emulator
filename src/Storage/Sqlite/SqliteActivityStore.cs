using Azure.Cosmos.LightEmulator.Core.Interfaces;

namespace Azure.Cosmos.LightEmulator.Storage.Sqlite;

/// <summary>
/// SQLite-backed activity log store.
/// </summary>
public class SqliteActivityStore : IActivityStore
{
    private readonly SqliteConnectionManager _connectionManager;

    public SqliteActivityStore(SqliteConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO activity (timestamp, method, path, status_code, request_charge, latency_ms, database_id, container_id)
            VALUES (@timestamp, @method, @path, @statusCode, @requestCharge, @latencyMs, @databaseId, @containerId)
        """;
        cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@method", entry.Method);
        cmd.Parameters.AddWithValue("@path", entry.Path);
        cmd.Parameters.AddWithValue("@statusCode", entry.StatusCode);
        cmd.Parameters.AddWithValue("@requestCharge", entry.RequestCharge);
        cmd.Parameters.AddWithValue("@latencyMs", entry.LatencyMs);
        cmd.Parameters.AddWithValue("@databaseId", (object?)entry.DatabaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@containerId", (object?)entry.ContainerId ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActivityEntry>> ListAsync(int maxItems = 1000, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT timestamp, method, path, status_code, request_charge, latency_ms, database_id, container_id FROM activity ORDER BY id DESC LIMIT @maxItems";
        cmd.Parameters.AddWithValue("@maxItems", maxItems);

        var entries = new List<ActivityEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new ActivityEntry
            {
                Timestamp = DateTimeOffset.Parse(reader.GetString(0)),
                Method = reader.GetString(1),
                Path = reader.GetString(2),
                StatusCode = reader.GetInt32(3),
                RequestCharge = reader.GetDouble(4),
                LatencyMs = reader.GetDouble(5),
                DatabaseId = reader.IsDBNull(6) ? null : reader.GetString(6),
                ContainerId = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return Task.FromResult<IReadOnlyList<ActivityEntry>>(entries);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM activity";
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task TrimAsync(int maxEntries, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM activity WHERE id NOT IN (SELECT id FROM activity ORDER BY id DESC LIMIT @max)";
        cmd.Parameters.AddWithValue("@max", maxEntries);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }
}
