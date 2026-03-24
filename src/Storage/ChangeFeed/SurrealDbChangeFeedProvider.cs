using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.Storage.ChangeFeed;

internal sealed class DbChangeFeedRecord
{
    [JsonPropertyName("databaseId")] public string DatabaseId { get; set; } = "";
    [JsonPropertyName("containerId")] public string ContainerId { get; set; } = "";
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("lsn")] public long Lsn { get; set; }
    [JsonPropertyName("changeType")] public int ChangeType { get; set; }
    [JsonPropertyName("bodyJson")] public string BodyJson { get; set; } = "";
    [JsonPropertyName("previousImageJson")] public string? PreviousImageJson { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("partitionKeyJson")] public string PartitionKeyJson { get; set; } = "";
}

/// <summary>
/// SurrealDB-backed change feed provider that persists document changes across restarts.
/// </summary>
public class SurrealDbChangeFeedProvider : IChangeFeedProvider
{
    private const string ChangeFeedTable = "cosmos_changefeed";
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();

    private readonly SurrealDbConnectionManager _connectionManager;

    public SurrealDbChangeFeedProvider(SurrealDbConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task RecordChangeAsync(
        string databaseId,
        string containerId,
        CosmosDocument document,
        ChangeType changeType,
        CosmosDocument? previousImage = null,
        CancellationToken ct = default)
    {
        var record = new DbChangeFeedRecord
        {
            DatabaseId = databaseId,
            ContainerId = containerId,
            DocumentId = document.Id,
            Lsn = document.Lsn,
            ChangeType = (int)changeType,
            BodyJson = document.Body.ToJsonString(),
            PreviousImageJson = previousImage?.Body.ToJsonString(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PartitionKeyJson = JsonSerializer.Serialize(document.PartitionKey.Components, JsonOptions)
        };

        var recordKey = $"{EncodeRecordKey(databaseId)}:{EncodeRecordKey(containerId)}:{EncodeRecordKey(document.Lsn.ToString())}";

        await ExecuteAsync(
            "CREATE $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(ChangeFeedTable, recordKey),
                ["data"] = record
            },
            ct);
    }

    public async Task<FeedResponse<ChangeFeedItem>> ReadChangeFeedAsync(
        string databaseId,
        string containerId,
        ChangeFeedOptions options,
        CancellationToken ct = default)
    {
        var allRecords = await SelectTableRecordsAsync<DbChangeFeedRecord>(ChangeFeedTable, ct);

        var items = allRecords
            .Where(r => string.Equals(r.DatabaseId, databaseId, StringComparison.Ordinal)
                     && string.Equals(r.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(r => r.Lsn)
            .ToList();

        if (items.Count == 0)
        {
            return new FeedResponse<ChangeFeedItem>
            {
                Resources = [],
                ContinuationToken = "0"
            };
        }

        long startLsn = 0;

        if (!string.IsNullOrEmpty(options.ContinuationToken) && long.TryParse(options.ContinuationToken, out var parsedLsn))
        {
            startLsn = parsedLsn;
        }
        else if (options.StartTime.HasValue)
        {
            var startTimeMs = options.StartTime.Value.ToUnixTimeMilliseconds();
            startLsn = items
                .Where(i => i.Timestamp >= startTimeMs)
                .Select(i => i.Lsn)
                .DefaultIfEmpty(0)
                .Min();
        }
        else if (!options.StartFromBeginning)
        {
            startLsn = items[^1].Lsn;
        }

        IEnumerable<DbChangeFeedRecord> filtered = items.Where(i => i.Lsn > startLsn);

        if (options.PartitionKey != null)
        {
            var targetPkJson = JsonSerializer.Serialize(options.PartitionKey.Components, JsonOptions);
            filtered = filtered.Where(i => string.Equals(i.PartitionKeyJson, targetPkJson, StringComparison.Ordinal));
        }

        if (!options.FullFidelity)
        {
            filtered = filtered.Where(i => i.ChangeType != (int)Core.Models.ChangeType.Delete);
        }

        var maxItems = options.MaxItemCount ?? 100;
        var result = filtered.Take(maxItems).Select(ToChangeFeedItem).ToList();

        var lastLsn = result.Count > 0 ? result[^1].Lsn : startLsn;

        return new FeedResponse<ChangeFeedItem>
        {
            Resources = result,
            ContinuationToken = lastLsn.ToString()
        };
    }

    private static ChangeFeedItem ToChangeFeedItem(DbChangeFeedRecord record)
    {
        var body = JsonNode.Parse(record.BodyJson)?.AsObject()
            ?? throw new InvalidOperationException("Unable to deserialize persisted change feed body.");

        var partitionKey = DeserializePartitionKey(record.PartitionKeyJson);

        CosmosDocument? previousImage = null;
        if (record.PreviousImageJson is not null)
        {
            var prevBody = JsonNode.Parse(record.PreviousImageJson)?.AsObject()
                ?? throw new InvalidOperationException("Unable to deserialize persisted previous image body.");
            previousImage = new CosmosDocument
            {
                Id = record.DocumentId,
                DatabaseId = record.DatabaseId,
                ContainerId = record.ContainerId,
                PartitionKey = partitionKey,
                Body = prevBody
            };
        }

        return new ChangeFeedItem
        {
            Document = new CosmosDocument
            {
                Id = record.DocumentId,
                DatabaseId = record.DatabaseId,
                ContainerId = record.ContainerId,
                PartitionKey = partitionKey,
                Body = body,
                Lsn = record.Lsn
            },
            Lsn = record.Lsn,
            ChangeType = (Core.Models.ChangeType)record.ChangeType,
            PreviousImage = previousImage,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(record.Timestamp)
        };
    }

    private static PartitionKeyValue DeserializePartitionKey(string json)
    {
        var values = JsonNode.Parse(json)?.AsArray()
            ?.Select(ConvertJsonNodeToValue)
            .ToList()
            ?? throw new InvalidOperationException("Unable to deserialize persisted partition key.");

        return new PartitionKeyValue { Components = values };
    }

    private static object? ConvertJsonNodeToValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => node.ToJsonString()
    };

    private static string EncodeRecordKey(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private async Task<ISurrealDbClient> GetClientAsync(CancellationToken ct)
    {
        await _connectionManager.InitializeAsync(ct);
        return _connectionManager.Client;
    }

    private async Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var response = await client.RawQuery(sql, parameters ?? EmptyParameters, ct);
        response.EnsureAllOks();
    }

    private async Task<List<T>> SelectTableRecordsAsync<T>(string table, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        return (await client.Select<T>(table, ct)).ToList();
    }
}
