using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.NoSql.Query;

/// <summary>
/// Parses INSERT / UPDATE / DELETE SQL statements and translates them into
/// <see cref="IDocumentStore"/> operations.  This is an emulator convenience
/// feature — the real Cosmos DB SQL API only supports SELECT.
/// </summary>
public sealed class DmlCommandService(IDocumentStore store, IQueryEngine queryEngine)
{
    private static readonly HashSet<string> SystemProperties =
        ["_rid", "_self", "_etag", "_ts", "_attachments"];

    /// <summary>
    /// Determines whether <paramref name="sql"/> (after comment-stripping and
    /// trimming) starts with a DML keyword.
    /// </summary>
    public static bool IsDml(string sql)
    {
        var clean = CosmosQueryEngine.StripSqlComments(sql.Trim()).TrimEnd(';').Trim();
        return clean.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || clean.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || clean.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Executes a DML statement and returns the affected documents in a
    /// standard <see cref="FeedResponse{T}"/> envelope.
    /// </summary>
    public async Task<FeedResponse<JsonObject>> ExecuteAsync(
        string databaseId,
        string containerId,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        var clean = CosmosQueryEngine.StripSqlComments(sql.Trim()).TrimEnd(';').Trim();

        if (clean.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            return await ExecuteInsertAsync(databaseId, containerId, clean, parameters, ct);

        if (clean.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            return await ExecuteUpdateAsync(databaseId, containerId, clean, parameters, ct);

        if (clean.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            return await ExecuteDeleteAsync(databaseId, containerId, clean, parameters, ct);

        throw CosmosEmulatorException.BadRequest("Unsupported statement. Use INSERT, UPDATE, DELETE, or SELECT.");
    }

    // ───────────────────────────── INSERT ─────────────────────────────

    private async Task<FeedResponse<JsonObject>> ExecuteInsertAsync(
        string databaseId,
        string containerId,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // INSERT INTO <alias> VALUES (<json-or-param>)
        var intoIndex = IndexOfKeyword(sql, "INTO", "INSERT".Length);
        if (intoIndex < 0)
            throw CosmosEmulatorException.BadRequest("INSERT syntax: INSERT INTO <alias> VALUES ({...}) or INSERT INTO <alias> VALUES (@param)");

        var valuesIndex = IndexOfKeyword(sql, "VALUES", intoIndex + "INTO".Length);
        if (valuesIndex < 0)
            throw CosmosEmulatorException.BadRequest("INSERT syntax: INSERT INTO <alias> VALUES ({...}) or INSERT INTO <alias> VALUES (@param)");

        var valuesPart = sql[(valuesIndex + "VALUES".Length)..].Trim();

        // Unwrap optional parentheses
        if (valuesPart.StartsWith('(') && valuesPart.EndsWith(')'))
            valuesPart = valuesPart[1..^1].Trim();

        JsonObject document;

        if (valuesPart.StartsWith('@'))
        {
            var paramName = valuesPart.Trim();
            if (parameters is null || !parameters.TryGetValue(paramName, out var paramValue))
                throw CosmosEmulatorException.BadRequest($"Parameter '{paramName}' is not defined.");

            document = paramValue switch
            {
                JsonObject jo => jo,
                JsonNode jn => jn.AsObject(),
                string s => JsonNode.Parse(s)?.AsObject()
                    ?? throw CosmosEmulatorException.BadRequest($"Parameter '{paramName}' is not a valid JSON object."),
                _ => throw CosmosEmulatorException.BadRequest($"Parameter '{paramName}' must be a JSON object.")
            };
        }
        else if (valuesPart.StartsWith('{'))
        {
            document = JsonNode.Parse(valuesPart)?.AsObject()
                ?? throw CosmosEmulatorException.BadRequest("VALUES must contain a valid JSON object.");
        }
        else
        {
            throw CosmosEmulatorException.BadRequest("INSERT VALUES must be a JSON object ({...}) or a parameter (@param).");
        }

        var created = await store.CreateDocumentAsync(databaseId, containerId, document, ct: ct);

        return new FeedResponse<JsonObject>
        {
            Resources = [created.ToResponseBody()]
        };
    }

    // ───────────────────────────── UPDATE ─────────────────────────────

    private async Task<FeedResponse<JsonObject>> ExecuteUpdateAsync(
        string databaseId,
        string containerId,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // UPDATE <alias> SET <assignments> [WHERE <conditions>]
        var setIndex = IndexOfKeyword(sql, "SET", "UPDATE".Length);
        if (setIndex < 0)
            throw CosmosEmulatorException.BadRequest("UPDATE syntax: UPDATE <alias> SET <field> = <value> [, ...] [WHERE <conditions>]");

        // Extract alias (between UPDATE and SET)
        var alias = sql["UPDATE".Length..setIndex].Trim();
        if (string.IsNullOrEmpty(alias))
            alias = "c";

        var whereIndex = IndexOfKeyword(sql, "WHERE", setIndex + "SET".Length);
        var setClause = whereIndex >= 0
            ? sql[(setIndex + "SET".Length)..whereIndex].Trim()
            : sql[(setIndex + "SET".Length)..].Trim();

        var assignments = ParseSetAssignments(setClause, alias, parameters);
        if (assignments.Count == 0)
            throw CosmosEmulatorException.BadRequest("UPDATE SET clause must contain at least one assignment.");

        // Find matching documents via SELECT
        var selectQuery = whereIndex >= 0
            ? $"SELECT * FROM {alias} {sql[whereIndex..]}"
            : $"SELECT * FROM {alias}";

        var matchedDocs = await queryEngine.ExecuteQueryAsync(
            databaseId, containerId, selectQuery, parameters,
            new QueryOptions { EnableCrossPartitionQuery = true, EnableScan = true },
            ct);

        if (matchedDocs.Resources.Count == 0)
            return new FeedResponse<JsonObject> { Resources = [] };

        var container = await store.GetContainerAsync(databaseId, containerId, ct);
        var results = new List<JsonObject>();

        foreach (var doc in matchedDocs.Resources)
        {
            var docId = doc["id"]?.GetValue<string>()
                ?? throw CosmosEmulatorException.BadRequest("Matched document has no 'id' field.");

            // Apply assignments
            foreach (var (path, value) in assignments)
                SetNestedValue(doc, path, value);

            var replaced = await store.ReplaceDocumentAsync(databaseId, containerId, docId, doc, ct: ct);
            results.Add(replaced.ToResponseBody());
        }

        return new FeedResponse<JsonObject> { Resources = results };
    }

    // ───────────────────────────── DELETE ─────────────────────────────

    private async Task<FeedResponse<JsonObject>> ExecuteDeleteAsync(
        string databaseId,
        string containerId,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // DELETE FROM <alias> [WHERE <conditions>]
        var fromIndex = IndexOfKeyword(sql, "FROM", "DELETE".Length);
        if (fromIndex < 0)
            throw CosmosEmulatorException.BadRequest("DELETE syntax: DELETE FROM <alias> [WHERE <conditions>]");

        var afterFrom = sql[(fromIndex + "FROM".Length)..].Trim();
        var whereIndex = IndexOfKeyword(sql, "WHERE", fromIndex + "FROM".Length);

        var alias = whereIndex >= 0
            ? sql[(fromIndex + "FROM".Length)..whereIndex].Trim()
            : afterFrom;
        if (string.IsNullOrEmpty(alias))
            alias = "c";

        // Find matching documents via SELECT
        var selectQuery = whereIndex >= 0
            ? $"SELECT * FROM {alias} {sql[whereIndex..]}"
            : $"SELECT * FROM {alias}";

        var matchedDocs = await queryEngine.ExecuteQueryAsync(
            databaseId, containerId, selectQuery, parameters,
            new QueryOptions { EnableCrossPartitionQuery = true, EnableScan = true },
            ct);

        if (matchedDocs.Resources.Count == 0)
            return new FeedResponse<JsonObject> { Resources = [] };

        var container = await store.GetContainerAsync(databaseId, containerId, ct);
        var results = new List<JsonObject>();

        foreach (var doc in matchedDocs.Resources)
        {
            var docId = doc["id"]?.GetValue<string>()
                ?? throw CosmosEmulatorException.BadRequest("Matched document has no 'id' field.");

            var pk = ExtractPartitionKey(doc, container.PartitionKey);

            // Snapshot before delete
            results.Add(doc.DeepClone().AsObject());
            await store.DeleteDocumentAsync(databaseId, containerId, docId, pk, ct);
        }

        return new FeedResponse<JsonObject> { Resources = results };
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    /// <summary>
    /// Case-insensitive keyword search that respects word boundaries.
    /// </summary>
    private static int IndexOfKeyword(string sql, string keyword, int startIndex)
    {
        var pos = startIndex;
        while (pos < sql.Length)
        {
            var idx = sql.IndexOf(keyword, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            var before = idx == 0 || !char.IsLetterOrDigit(sql[idx - 1]);
            var after = idx + keyword.Length >= sql.Length || !char.IsLetterOrDigit(sql[idx + keyword.Length]);
            if (before && after) return idx;

            pos = idx + 1;
        }

        return -1;
    }

    /// <summary>
    /// Parses "c.field1 = value1, c.field2 = value2" into a list of (path, value) pairs.
    /// </summary>
    private static List<(string[] Path, JsonNode? Value)> ParseSetAssignments(
        string setClause,
        string alias,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        var assignments = new List<(string[] Path, JsonNode? Value)>();

        foreach (var part in SplitTopLevel(setClause, ','))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx < 0)
                throw CosmosEmulatorException.BadRequest($"Invalid SET assignment: '{part.Trim()}'. Expected 'field = value'.");

            var lhs = part[..eqIdx].Trim();
            var rhs = part[(eqIdx + 1)..].Trim();

            // Strip alias prefix (e.g. "c.name" → "name")
            if (lhs.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase))
                lhs = lhs[(alias.Length + 1)..];

            var pathParts = lhs.Split('.');
            if (pathParts.Length == 0 || pathParts.Any(string.IsNullOrWhiteSpace))
                throw CosmosEmulatorException.BadRequest($"Invalid field path: '{lhs}'.");

            var value = ParseScalarValue(rhs, parameters);
            assignments.Add((pathParts, value));
        }

        return assignments;
    }

    /// <summary>
    /// Parses a scalar value from the right-hand side of a SET assignment.
    /// Supports: strings, numbers, booleans, null, and parameters.
    /// </summary>
    private static JsonNode? ParseScalarValue(string rhs, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(rhs))
            throw CosmosEmulatorException.BadRequest("SET assignment value cannot be empty.");

        // Parameter reference
        if (rhs.StartsWith('@'))
        {
            if (parameters is null || !parameters.TryGetValue(rhs, out var val))
                throw CosmosEmulatorException.BadRequest($"Parameter '{rhs}' is not defined.");

            return val switch
            {
                null => null,
                JsonNode jn => jn.DeepClone(),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                float f => JsonValue.Create(f),
                _ => JsonNode.Parse(JsonSerializer.Serialize(val))
            };
        }

        // null
        if (rhs.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;

        // true / false
        if (rhs.Equals("true", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(true);
        if (rhs.Equals("false", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(false);

        // Quoted string
        if ((rhs.StartsWith('"') && rhs.EndsWith('"')) || (rhs.StartsWith('\'') && rhs.EndsWith('\'')))
            return JsonValue.Create(rhs[1..^1]);

        // JSON object or array
        if (rhs.StartsWith('{') || rhs.StartsWith('['))
        {
            return JsonNode.Parse(rhs)
                ?? throw CosmosEmulatorException.BadRequest($"Invalid JSON value: {rhs}");
        }

        // Number
        if (rhs.Contains('.'))
        {
            if (double.TryParse(rhs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return JsonValue.Create(d);
        }
        else
        {
            if (long.TryParse(rhs, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
                return JsonValue.Create(l);
        }

        throw CosmosEmulatorException.BadRequest($"Cannot parse SET value: '{rhs}'. Use a quoted string, number, boolean, null, JSON, or @parameter.");
    }

    /// <summary>
    /// Sets a nested value on a <see cref="JsonObject"/> given a dotted path.
    /// </summary>
    private static void SetNestedValue(JsonObject doc, string[] path, JsonNode? value)
    {
        var current = doc;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current[path[i]] is not JsonObject child)
            {
                child = new JsonObject();
                current[path[i]] = child;
            }
            current = child;
        }

        current[path[^1]] = value?.DeepClone();
    }

    /// <summary>
    /// Splits a string on a delimiter, respecting parentheses and quotes.
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string input, char delimiter)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var start = 0;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '\'' && !inDouble) inSingle = !inSingle;
            else if (ch == '"' && !inSingle) inDouble = !inDouble;
            else if (!inSingle && !inDouble)
            {
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') depth--;
                else if (ch == delimiter && depth == 0)
                {
                    yield return input[start..i];
                    start = i + 1;
                }
            }
        }

        if (start < input.Length)
            yield return input[start..];
    }

    private static PartitionKeyValue ExtractPartitionKey(JsonObject document, PartitionKeyDefinition pkDef)
    {
        var values = new List<object?>();
        foreach (var path in pkDef.Paths)
        {
            var propertyName = path.TrimStart('/');
            values.Add(ConvertJsonNodeToValue(document[propertyName]));
        }
        return new PartitionKeyValue { Components = values };
    }

    private static object? ConvertJsonNodeToValue(JsonNode? node)
    {
        if (node is null) return null;

        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<long>(out var l)) return l;
            if (jv.TryGetValue<double>(out var d)) return d;
        }

        return node.ToJsonString();
    }
}
