using System.Text.Json;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Storage.Sqlite;

/// <summary>
/// SQLite-backed change feed provider that tracks document changes.
/// </summary>
public class SqliteChangeFeedProvider : IChangeFeedProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolverChain = { new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }
    };

    private readonly SqliteConnectionManager _connectionManager;

    public SqliteChangeFeedProvider(SqliteConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public Task RecordChangeAsync(
        string databaseId,
        string containerId,
        CosmosDocument document,
        ChangeType changeType,
        CosmosDocument? previousImage = null,
        CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO changefeed (database_id, container_id, document_id, lsn, change_type, body_json, previous_image_json, timestamp, partition_key_json)
            VALUES (@databaseId, @containerId, @documentId, @lsn, @changeType, @bodyJson, @previousImageJson, @timestamp, @partitionKeyJson)
        """;
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@containerId", containerId);
        cmd.Parameters.AddWithValue("@documentId", document.Id);
        cmd.Parameters.AddWithValue("@lsn", document.Lsn);
        cmd.Parameters.AddWithValue("@changeType", (int)changeType);
        cmd.Parameters.AddWithValue("@bodyJson", document.Body.ToJsonString());
        cmd.Parameters.AddWithValue("@previousImageJson", previousImage is not null
            ? JsonSerializer.Serialize(SerializeDocumentForChangeFeed(previousImage), JsonOptions)
            : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@partitionKeyJson", DocumentStoreHelpers.SerializePartitionKey(document.PartitionKey));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<FeedResponse<ChangeFeedItem>> ReadChangeFeedAsync(
        string databaseId,
        string containerId,
        ChangeFeedOptions options,
        CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();

        long startLsn = 0;

        if (!string.IsNullOrEmpty(options.ContinuationToken) && long.TryParse(options.ContinuationToken, out var parsedLsn))
        {
            startLsn = parsedLsn;
        }
        else if (options.StartTime.HasValue)
        {
            using var timeCmd = connection.CreateCommand();
            timeCmd.CommandText = """
                SELECT MIN(lsn) FROM changefeed
                WHERE database_id = @databaseId AND container_id = @containerId AND timestamp >= @startTime
            """;
            timeCmd.Parameters.AddWithValue("@databaseId", databaseId);
            timeCmd.Parameters.AddWithValue("@containerId", containerId);
            timeCmd.Parameters.AddWithValue("@startTime", options.StartTime.Value.ToString("O"));
            var result = timeCmd.ExecuteScalar();
            if (result is long minLsn)
                startLsn = minLsn > 0 ? minLsn - 1 : 0;
        }
        else if (!options.StartFromBeginning)
        {
            using var maxCmd = connection.CreateCommand();
            maxCmd.CommandText = """
                SELECT COALESCE(MAX(lsn), 0) FROM changefeed
                WHERE database_id = @databaseId AND container_id = @containerId
            """;
            maxCmd.Parameters.AddWithValue("@databaseId", databaseId);
            maxCmd.Parameters.AddWithValue("@containerId", containerId);
            startLsn = (long)(maxCmd.ExecuteScalar() ?? 0L);
        }

        var maxItems = options.MaxItemCount ?? 100;

        using var cmd = connection.CreateCommand();
        var sql = """
            SELECT document_id, lsn, change_type, body_json, previous_image_json, timestamp, partition_key_json
            FROM changefeed
            WHERE database_id = @databaseId AND container_id = @containerId AND lsn > @startLsn
        """;

        if (options.PartitionKey is not null)
            sql += " AND partition_key_json = @partitionKey";

        if (!options.FullFidelity)
            sql += $" AND change_type != {(int)ChangeType.Delete}";

        sql += " ORDER BY lsn ASC LIMIT @maxItems";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@containerId", containerId);
        cmd.Parameters.AddWithValue("@startLsn", startLsn);
        cmd.Parameters.AddWithValue("@maxItems", maxItems);

        if (options.PartitionKey is not null)
            cmd.Parameters.AddWithValue("@partitionKey", DocumentStoreHelpers.SerializePartitionKey(options.PartitionKey));

        var items = new List<ChangeFeedItem>();
        long lastLsn = startLsn;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var docId = reader.GetString(0);
            var lsn = reader.GetInt64(1);
            var changeType = (ChangeType)reader.GetInt32(2);
            var bodyJson = reader.GetString(3);
            var previousImageJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            var timestamp = DateTimeOffset.Parse(reader.GetString(5));
            var pkJson = reader.GetString(6);

            var body = DocumentStoreHelpers.DeserializeJsonObject(bodyJson);
            var pk = DocumentStoreHelpers.DeserializePartitionKey(pkJson);

            var document = new CosmosDocument
            {
                Id = docId,
                DatabaseId = databaseId,
                ContainerId = containerId,
                PartitionKey = pk,
                Body = body,
                Lsn = lsn,
                Self = $"dbs/{databaseId}/colls/{containerId}/docs/{docId}/"
            };

            CosmosDocument? previousImage = null;
            if (previousImageJson is not null)
            {
                var prevData = JsonSerializer.Deserialize<ChangeFeedDocumentData>(previousImageJson, JsonOptions);
                if (prevData is not null)
                {
                    previousImage = new CosmosDocument
                    {
                        Id = prevData.Id ?? docId,
                        DatabaseId = databaseId,
                        ContainerId = containerId,
                        PartitionKey = pk,
                        Body = prevData.BodyJson is not null
                            ? DocumentStoreHelpers.DeserializeJsonObject(prevData.BodyJson)
                            : new System.Text.Json.Nodes.JsonObject(),
                        Lsn = prevData.Lsn
                    };
                }
            }

            items.Add(new ChangeFeedItem
            {
                Document = document,
                Lsn = lsn,
                ChangeType = changeType,
                PreviousImage = previousImage,
                Timestamp = timestamp
            });

            lastLsn = lsn;
        }

        return Task.FromResult(new FeedResponse<ChangeFeedItem>
        {
            Resources = items,
            ContinuationToken = lastLsn.ToString()
        });
    }

    private static ChangeFeedDocumentData SerializeDocumentForChangeFeed(CosmosDocument doc) => new()
    {
        Id = doc.Id,
        BodyJson = doc.Body.ToJsonString(),
        Lsn = doc.Lsn
    };

    private sealed class ChangeFeedDocumentData
    {
        public string? Id { get; set; }
        public string? BodyJson { get; set; }
        public long Lsn { get; set; }
    }
}
