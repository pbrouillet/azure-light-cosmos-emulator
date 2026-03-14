using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;

namespace Azure.Cosmos.LightEmulator.NoSql;

/// <summary>
/// Temporary query engine that supports a minimal Cosmos SQL subset.
/// </summary>
public sealed class StubQueryEngine : IQueryEngine
{
    private static readonly Regex SelectAllPattern = new(
        @"^\s*SELECT\s+\*\s+FROM\s+c\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SelectByIdPattern = new(
        @"^\s*SELECT\s+\*\s+FROM\s+c\s+WHERE\s+c\.id\s*=\s*(?<value>@[A-Za-z0-9_]+|'[^']*')\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IDocumentStore _documentStore;

    public StubQueryEngine(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    public Task<FeedResponse<JsonObject>> ExecuteQueryAsync(
        string databaseId,
        string containerId,
        string query,
        IReadOnlyDictionary<string, object?>? parameters = null,
        QueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw CosmosEmulatorException.BadRequest("Query text is required.");
        }

        var documents = GetContainerDocuments(databaseId, containerId);

        if (options?.PartitionKey is not null)
        {
            documents = documents.Where(document => document.PartitionKey.Equals(options.PartitionKey));
        }

        if (SelectAllPattern.IsMatch(query))
        {
            return Task.FromResult(ToFeedResponse(databaseId, containerId, documents, options));
        }

        var selectByIdMatch = SelectByIdPattern.Match(query);
        if (selectByIdMatch.Success)
        {
            var id = ResolveId(selectByIdMatch.Groups["value"].Value, parameters);
            documents = documents.Where(document => string.Equals(document.Id, id, StringComparison.Ordinal));
            return Task.FromResult(ToFeedResponse(databaseId, containerId, documents, options));
        }

        throw CosmosEmulatorException.BadRequest(
            "StubQueryEngine currently supports only 'SELECT * FROM c' and 'SELECT * FROM c WHERE c.id = @id' queries.");
    }

    private FeedResponse<JsonObject> ToFeedResponse(
        string databaseId,
        string containerId,
        IEnumerable<CosmosDocument> documents,
        QueryOptions? options)
    {
        var ordered = documents.OrderBy(document => document.Timestamp).ThenBy(document => document.Id).ToList();
        var skip = ParseContinuationToken(options?.ContinuationToken);
        var take = options?.MaxItemCount ?? ordered.Count;
        var page = ordered
            .Skip(skip)
            .Take(take)
            .Select(document => document.ToResponseBody())
            .ToList();

        return new FeedResponse<JsonObject>
        {
            Rid = $"{databaseId}/{containerId}",
            Resources = page,
            ContinuationToken = skip + page.Count < ordered.Count
                ? (skip + page.Count).ToString(CultureInfo.InvariantCulture)
                : null
        };
    }

    private IEnumerable<CosmosDocument> GetContainerDocuments(string databaseId, string containerId)
    {
        if (_documentStore is not SurrealDbDocumentStore surrealStore)
        {
            throw CosmosEmulatorException.BadRequest("StubQueryEngine requires SurrealDbDocumentStore.");
        }

        var documentsField = typeof(SurrealDbDocumentStore).GetField("_documents", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw CosmosEmulatorException.InternalServerError("Unable to access the document store for query execution.");

        if (documentsField.GetValue(surrealStore) is not IEnumerable<KeyValuePair<string, CosmosDocument>> documents)
        {
            throw CosmosEmulatorException.InternalServerError("Document store contents are unavailable for query execution.");
        }

        return documents
            .Select(entry => entry.Value)
            .Where(document => string.Equals(document.DatabaseId, databaseId, StringComparison.Ordinal)
                && string.Equals(document.ContainerId, containerId, StringComparison.Ordinal))
            .ToList();
    }

    private static string ResolveId(string valueToken, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (valueToken.StartsWith("@", StringComparison.Ordinal))
        {
            if (parameters is null)
            {
                throw CosmosEmulatorException.BadRequest($"Missing query parameter '{valueToken}'.");
            }

            if (!parameters.TryGetValue(valueToken, out var value)
                && !parameters.TryGetValue(valueToken.TrimStart('@'), out value))
            {
                throw CosmosEmulatorException.BadRequest($"Missing query parameter '{valueToken}'.");
            }

            return NormalizeValue(value, valueToken);
        }

        return valueToken.Trim('"', '\'');
    }

    private static string NormalizeValue(object? value, string parameterName)
    {
        return value switch
        {
            null => throw CosmosEmulatorException.BadRequest($"Query parameter '{parameterName}' cannot be null."),
            JsonNode node when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
            JsonNode node => node.ToJsonString(),
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonElement element => element.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                 ?? throw CosmosEmulatorException.BadRequest($"Query parameter '{parameterName}' is invalid.")
        };
    }

    private static int ParseContinuationToken(string? continuationToken)
    {
        if (string.IsNullOrWhiteSpace(continuationToken))
        {
            return 0;
        }

        if (int.TryParse(continuationToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var continuation) && continuation >= 0)
        {
            return continuation;
        }

        throw CosmosEmulatorException.BadRequest("The continuation token is invalid.");
    }
}
