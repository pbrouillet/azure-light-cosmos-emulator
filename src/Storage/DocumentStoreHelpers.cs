using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Storage;

/// <summary>
/// Shared helper methods used by all IDocumentStore implementations.
/// </summary>
public static class DocumentStoreHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolverChain = { new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }
    };

    public const int MaxDocumentSizeBytes = 2 * 1024 * 1024; // 2 MB

    public static PartitionKeyValue ExtractPartitionKey(JsonObject document, PartitionKeyDefinition pkDef)
    {
        var values = new List<object?>();
        foreach (var path in pkDef.Paths)
        {
            var propertyName = path.TrimStart('/');
            values.Add(ConvertJsonNodeToValue(document[propertyName]));
        }
        return new PartitionKeyValue { Components = values };
    }

    public static int? ExtractTimeToLive(JsonObject document)
    {
        if (document["ttl"] is null)
            return null;
        return document["ttl"]?.GetValue<int>();
    }

    public static void EnforceDocumentSizeLimit(JsonObject document)
    {
        var size = document.ToJsonString().Length;
        if (size > MaxDocumentSizeBytes)
        {
            throw CosmosEmulatorException.EntityTooLarge(
                $"The document size ({size} bytes) exceeds the maximum allowed size ({MaxDocumentSizeBytes} bytes).");
        }
    }

    public static string SerializePartitionKey(PartitionKeyValue partitionKey) =>
        JsonSerializer.Serialize(partitionKey.Components, JsonOptions);

    public static PartitionKeyValue DeserializePartitionKey(string json)
    {
        var values = JsonNode.Parse(json)?.AsArray()
            ?.Select(ConvertJsonNodeToValue)
            .ToList()
            ?? throw new InvalidOperationException("Unable to deserialize persisted partition key.");
        return new PartitionKeyValue { Components = values };
    }

    public static string? SerializeNullable<T>(T? value)
        where T : class => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    public static T DeserializeRequired<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Unable to deserialize {typeof(T).Name}.");

    public static T? DeserializeNullable<T>(string? json)
        where T : class => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<T>(json, JsonOptions);

    public static JsonObject DeserializeJsonObject(string json) =>
        JsonNode.Parse(json)?.AsObject()
        ?? throw new InvalidOperationException("Unable to deserialize persisted document body.");

    public static object? ConvertJsonNodeToValue(JsonNode? node) => node switch
    {
        null => null,
        _ when node.GetValueKind() == JsonValueKind.Null => null,
        _ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
        _ when node.GetValueKind() == JsonValueKind.Number => node.GetValue<double>(),
        _ when node.GetValueKind() == JsonValueKind.True => true,
        _ when node.GetValueKind() == JsonValueKind.False => false,
        _ => node.ToJsonString()
    };

    public static string EncodeRecordKey(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string GenerateResourceToken(CosmosPermission permission)
    {
        var payload = $"{permission.Resource}:{permission.PermissionMode}:{permission.Id}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(permission.Rid));
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return $"type=resource&ver=1.0&sig={sig};";
    }

    // ─── Patch operations ───────────────────────────────────────────

    public static void ApplyPatchOperations(JsonObject document, IReadOnlyList<PatchOperation> operations)
    {
        foreach (var op in operations)
        {
            var segments = op.Path.TrimStart('/').Split('/');
            switch (op.Op.ToLowerInvariant())
            {
                case "add":
                case "set":
                    SetNestedValue(document, segments, ConvertPatchValue(op.Value));
                    break;
                case "replace":
                    if (!TryGetParentAndKey(document, segments, out var replaceParent, out var replaceKey))
                        throw CosmosEmulatorException.BadRequest($"Path '{op.Path}' does not exist for replace operation.");
                    if (replaceParent is JsonObject replaceObj && !replaceObj.ContainsKey(replaceKey))
                        throw CosmosEmulatorException.BadRequest($"Path '{op.Path}' does not exist for replace operation.");
                    SetNestedValue(document, segments, ConvertPatchValue(op.Value));
                    break;
                case "remove":
                    if (TryGetParentAndKey(document, segments, out var removeParent, out var removeKey))
                    {
                        if (removeParent is JsonObject removeObj)
                            removeObj.Remove(removeKey);
                        else if (removeParent is JsonArray removeArr && int.TryParse(removeKey, out var idx))
                            removeArr.RemoveAt(idx);
                    }
                    break;
                case "incr":
                    IncrementValue(document, segments, op.Value);
                    break;
                case "move":
                    if (string.IsNullOrEmpty(op.From))
                        throw CosmosEmulatorException.BadRequest("Move operation requires 'from' property.");
                    var fromSegments = op.From.TrimStart('/').Split('/');
                    var value = GetNestedValue(document, fromSegments);
                    if (TryGetParentAndKey(document, fromSegments, out var moveFromParent, out var moveFromKey)
                        && moveFromParent is JsonObject moveFromObj)
                        moveFromObj.Remove(moveFromKey);
                    SetNestedValue(document, segments, value?.DeepClone());
                    break;
                default:
                    throw CosmosEmulatorException.BadRequest($"Unknown patch operation: '{op.Op}'.");
            }
        }
    }

    public static bool EvaluatePatchCondition(JsonObject document, string condition)
    {
        var trimmed = condition.Trim();
        if (trimmed.StartsWith("from", StringComparison.OrdinalIgnoreCase))
        {
            var whereIdx = trimmed.IndexOf("where", StringComparison.OrdinalIgnoreCase);
            if (whereIdx < 0) return true;
            trimmed = trimmed[(whereIdx + "where".Length)..].Trim();
        }

        var match = System.Text.RegularExpressions.Regex.Match(trimmed,
            @"^(\w+)\.(\w+(?:\.\w+)*)\s*(=|!=|>|<|>=|<=)\s*(?:'([^']*)'|(\d+(?:\.\d+)?))$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return true;

        var propPath = match.Groups[2].Value;
        var op = match.Groups[3].Value;
        var strVal = match.Groups[4].Success ? match.Groups[4].Value : null;
        var numVal = match.Groups[5].Success ? double.Parse(match.Groups[5].Value) : (double?)null;

        var propSegments = propPath.Split('.');
        JsonNode? current = document;
        foreach (var seg in propSegments)
        {
            if (current is JsonObject obj) current = obj[seg];
            else return false;
        }

        if (current is null) return op == "!=";

        if (strVal is not null)
        {
            var actualStr = current.GetValueKind() == JsonValueKind.String ? current.GetValue<string>() : current.ToJsonString();
            return op switch
            {
                "=" => string.Equals(actualStr, strVal, StringComparison.Ordinal),
                "!=" => !string.Equals(actualStr, strVal, StringComparison.Ordinal),
                _ => false
            };
        }

        if (numVal is not null && current.GetValueKind() == JsonValueKind.Number)
        {
            var actualNum = current.GetValue<double>();
            return op switch
            {
                "=" => Math.Abs(actualNum - numVal.Value) < 0.0001,
                "!=" => Math.Abs(actualNum - numVal.Value) >= 0.0001,
                ">" => actualNum > numVal.Value,
                "<" => actualNum < numVal.Value,
                ">=" => actualNum >= numVal.Value,
                "<=" => actualNum <= numVal.Value,
                _ => false
            };
        }

        return false;
    }

    // ─── Unique key enforcement ─────────────────────────────────────

    public static void EnforceUniqueKeyPolicy(
        CosmosContainer container,
        IEnumerable<CosmosDocument> partitionDocuments,
        PartitionKeyValue partitionKey,
        JsonObject document,
        string? excludeDocumentId)
    {
        if (container.UniqueKeyPolicy?.UniqueKeys is not { Count: > 0 })
            return;

        var partitionDocs = partitionDocuments
            .Where(d => d.PartitionKey.Equals(partitionKey))
            .Where(d => excludeDocumentId is null || !string.Equals(d.Id, excludeDocumentId, StringComparison.Ordinal));

        foreach (var uniqueKey in container.UniqueKeyPolicy.UniqueKeys)
        {
            var newValues = uniqueKey.Paths.Select(path => ExtractValueAtPath(document, path)).ToList();

            foreach (var existing in partitionDocs)
            {
                var existingValues = uniqueKey.Paths.Select(path => ExtractValueAtPath(existing.Body, path)).ToList();
                if (UniqueKeyValuesMatch(newValues, existingValues))
                {
                    var pathsStr = string.Join(", ", uniqueKey.Paths);
                    throw CosmosEmulatorException.Conflict("Document",
                        $"Unique key constraint violation for paths: {pathsStr}");
                }
            }
        }
    }

    public static string? ExtractValueAtPath(JsonObject doc, string path)
    {
        var segments = path.TrimStart('/').Split('/');
        JsonNode? current = doc;
        foreach (var segment in segments)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
                current = next;
            else
                return null;
        }
        return current?.ToJsonString();
    }

    // ─── Private helpers ────────────────────────────────────────────

    private static bool UniqueKeyValuesMatch(List<string?> a, List<string?> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static void SetNestedValue(JsonObject root, string[] segments, JsonNode? value)
    {
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current is JsonObject obj)
            {
                if (!obj.ContainsKey(segments[i]))
                    obj[segments[i]] = new JsonObject();
                current = obj[segments[i]]!;
            }
            else if (current is JsonArray arr && int.TryParse(segments[i], out var idx))
            {
                current = arr[idx]!;
            }
        }

        var lastKey = segments[^1];
        if (current is JsonObject parentObj)
            parentObj[lastKey] = value;
        else if (current is JsonArray parentArr && int.TryParse(lastKey, out var arrIdx))
            parentArr[arrIdx] = value;
    }

    private static JsonNode? GetNestedValue(JsonObject root, string[] segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is JsonObject obj)
                current = obj[segment];
            else if (current is JsonArray arr && int.TryParse(segment, out var idx))
                current = arr[idx];
            else
                return null;
        }
        return current;
    }

    private static bool TryGetParentAndKey(JsonObject root, string[] segments, out JsonNode? parent, out string key)
    {
        parent = root;
        key = segments[^1];
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (parent is JsonObject obj)
                parent = obj[segments[i]];
            else if (parent is JsonArray arr && int.TryParse(segments[i], out var idx))
                parent = arr[idx];
            else
                return false;
        }
        return parent is not null;
    }

    private static void IncrementValue(JsonObject root, string[] segments, object? incrementBy)
    {
        var current = GetNestedValue(root, segments);
        if (current is null)
        {
            SetNestedValue(root, segments, ConvertPatchValue(incrementBy));
            return;
        }

        var currentVal = current.GetValueKind() == JsonValueKind.Number
            ? current.GetValue<double>()
            : throw CosmosEmulatorException.BadRequest($"Cannot increment non-numeric value at '{string.Join("/", segments)}'.");

        var incrVal = incrementBy switch
        {
            int i => (double)i,
            long l => (double)l,
            double d => d,
            float f => (double)f,
            JsonNode node when node.GetValueKind() == JsonValueKind.Number => node.GetValue<double>(),
            _ => throw CosmosEmulatorException.BadRequest("Increment value must be a number.")
        };

        SetNestedValue(root, segments, JsonValue.Create(currentVal + incrVal));
    }

    private static JsonNode? ConvertPatchValue(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        _ => JsonNode.Parse(JsonSerializer.Serialize(value, value.GetType(), JsonOptions))
    };
}
