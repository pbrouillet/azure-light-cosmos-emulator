using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Storage.Sqlite;

/// <summary>
/// SQLite-backed query telemetry store.
/// </summary>
public class SqliteQueryTelemetryStore : IQueryTelemetryStore
{
    private readonly SqliteConnectionManager _connectionManager;

    public SqliteQueryTelemetryStore(SqliteConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public Task RecordAsync(QueryTelemetryEntry entry, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO query_telemetry
                (id, timestamp, database_id, container_id, sql_text, partition_key, consistency_level,
                 request_charge, latency_ms, item_count, status_code, activity_id, is_cross_partition, query_plan)
            VALUES
                (@id, @timestamp, @databaseId, @containerId, @sqlText, @partitionKey, @consistencyLevel,
                 @requestCharge, @latencyMs, @itemCount, @statusCode, @activityId, @isCrossPartition, @queryPlan)
        """;
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@databaseId", entry.DatabaseId);
        cmd.Parameters.AddWithValue("@containerId", entry.ContainerId);
        cmd.Parameters.AddWithValue("@sqlText", entry.SqlText);
        cmd.Parameters.AddWithValue("@partitionKey", (object?)entry.PartitionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@consistencyLevel", entry.ConsistencyLevel);
        cmd.Parameters.AddWithValue("@requestCharge", entry.RequestCharge);
        cmd.Parameters.AddWithValue("@latencyMs", entry.LatencyMs);
        cmd.Parameters.AddWithValue("@itemCount", entry.ItemCount);
        cmd.Parameters.AddWithValue("@statusCode", entry.StatusCode);
        cmd.Parameters.AddWithValue("@activityId", entry.ActivityId);
        cmd.Parameters.AddWithValue("@isCrossPartition", entry.IsCrossPartition ? 1 : 0);
        cmd.Parameters.AddWithValue("@queryPlan", (object?)entry.QueryPlan ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueryTelemetryEntry>> ListAsync(
        string? databaseId = null,
        string? containerId = null,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();

        var sql = "SELECT id, timestamp, database_id, container_id, sql_text, partition_key, consistency_level, request_charge, latency_ms, item_count, status_code, activity_id, is_cross_partition, query_plan FROM query_telemetry";
        var conditions = new List<string>();

        if (!string.IsNullOrEmpty(databaseId))
        {
            conditions.Add("database_id = @databaseId");
            cmd.Parameters.AddWithValue("@databaseId", databaseId);
        }

        if (!string.IsNullOrEmpty(containerId))
        {
            conditions.Add("container_id = @containerId");
            cmd.Parameters.AddWithValue("@containerId", containerId);
        }

        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);

        sql += " ORDER BY timestamp DESC LIMIT @maxItems";
        cmd.Parameters.AddWithValue("@maxItems", maxItems);

        cmd.CommandText = sql;

        var entries = new List<QueryTelemetryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new QueryTelemetryEntry
            {
                Id = reader.GetString(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1)),
                DatabaseId = reader.GetString(2),
                ContainerId = reader.GetString(3),
                SqlText = reader.GetString(4),
                PartitionKey = reader.IsDBNull(5) ? null : reader.GetString(5),
                ConsistencyLevel = reader.GetString(6),
                RequestCharge = reader.GetDouble(7),
                LatencyMs = reader.GetInt64(8),
                ItemCount = reader.GetInt32(9),
                StatusCode = reader.GetInt32(10),
                ActivityId = reader.GetString(11),
                IsCrossPartition = reader.GetInt32(12) == 1,
                QueryPlan = reader.IsDBNull(13) ? null : reader.GetString(13)
            });
        }

        return Task.FromResult<IReadOnlyList<QueryTelemetryEntry>>(entries);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM query_telemetry";
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task TrimAsync(int maxEntries, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM query_telemetry WHERE id NOT IN (SELECT id FROM query_telemetry ORDER BY id DESC LIMIT @max)";
        cmd.Parameters.AddWithValue("@max", maxEntries);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }
}
