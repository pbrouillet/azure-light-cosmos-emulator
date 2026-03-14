using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.NoSql.Query;

/// <summary>
/// Practical in-memory evaluator for a subset of Cosmos DB SQL.
/// </summary>
public sealed class CosmosQueryEngine : IQueryEngine
{
    private static readonly object UndefinedValue = new();

    private readonly IDocumentStore _documentStore;

    public CosmosQueryEngine(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    public async Task<FeedResponse<JsonObject>> ExecuteQueryAsync(
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

        ct.ThrowIfCancellationRequested();

        var plan = ParseQuery(query, parameters);
        _ = await _documentStore.GetContainerAsync(databaseId, containerId, ct);

        var allDocuments = await _documentStore.ListDocumentsAsync(databaseId, containerId, ct);
        var documents = allDocuments.Resources;

        if (options?.PartitionKey is not null)
        {
            documents = documents
                .Where(document => document.PartitionKey.Equals(options.PartitionKey))
                .ToList();
        }

        var matchingDocuments = new List<DocumentContext>();
        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();

            var responseBody = document.ToResponseBody();
            if (MatchesFilters(responseBody, plan.Filters, parameters))
            {
                matchingDocuments.Add(new DocumentContext(document, responseBody));
            }
        }

        matchingDocuments = ApplyOrdering(matchingDocuments, plan);
        matchingDocuments = ApplyWindowing(matchingDocuments, plan);

        List<JsonObject> projectedResults;
        if (plan.ProjectionType == ProjectionType.Count)
        {
            projectedResults =
            [
                new JsonObject
                {
                    ["$1"] = matchingDocuments.Count
                }
            ];
        }
        else
        {
            projectedResults = matchingDocuments
                .Select(context => Project(context.ResponseBody, plan.Projection))
                .ToList();
        }

        var continuationIndex = ParseContinuationToken(options?.ContinuationToken);
        var takeCount = options?.MaxItemCount is > 0 ? options.MaxItemCount.Value : projectedResults.Count;

        var page = projectedResults
            .Skip(continuationIndex)
            .Take(takeCount)
            .Select(result => result.DeepClone().AsObject())
            .ToList();

        var nextIndex = continuationIndex + page.Count;
        return new FeedResponse<JsonObject>
        {
            Rid = $"{databaseId}/{containerId}",
            Resources = page,
            ContinuationToken = nextIndex < projectedResults.Count
                ? nextIndex.ToString(CultureInfo.InvariantCulture)
                : null
        };
    }

    private static QueryPlan ParseQuery(string query, IReadOnlyDictionary<string, object?>? parameters)
    {
        var trimmedQuery = query.Trim().TrimEnd(';').Trim();
        if (!trimmedQuery.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            throw CosmosEmulatorException.BadRequest("Only SELECT queries are supported.");
        }

        var fromIndex = FindTopLevelKeyword(trimmedQuery, "FROM", 0);
        if (fromIndex < 0)
        {
            throw CosmosEmulatorException.BadRequest("Queries must include a FROM clause.");
        }

        var selectClause = trimmedQuery["SELECT".Length..fromIndex].Trim();
        var whereIndex = FindTopLevelKeyword(trimmedQuery, "WHERE", fromIndex + "FROM".Length);
        var orderByIndex = FindTopLevelKeyword(trimmedQuery, "ORDER BY", fromIndex + "FROM".Length);
        var offsetIndex = FindTopLevelKeyword(trimmedQuery, "OFFSET", fromIndex + "FROM".Length);

        var clauseIndexes = new[] { whereIndex, orderByIndex, offsetIndex }
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToArray();
        var fromClauseEnd = clauseIndexes.FirstOrDefault(trimmedQuery.Length);
        var fromClause = trimmedQuery[(fromIndex + "FROM".Length)..fromClauseEnd].Trim();
        if (!string.Equals(fromClause, "c", StringComparison.OrdinalIgnoreCase))
        {
            throw CosmosEmulatorException.BadRequest("Only 'FROM c' queries are supported.");
        }

        string? whereClause = null;
        if (whereIndex >= 0)
        {
            var whereEnd = new[] { orderByIndex, offsetIndex }
                .Where(index => index > whereIndex)
                .OrderBy(index => index)
                .FirstOrDefault(trimmedQuery.Length);
            whereClause = trimmedQuery[(whereIndex + "WHERE".Length)..whereEnd].Trim();
        }

        string? orderByClause = null;
        if (orderByIndex >= 0)
        {
            var orderByEnd = new[] { offsetIndex }
                .Where(index => index > orderByIndex)
                .OrderBy(index => index)
                .FirstOrDefault(trimmedQuery.Length);
            orderByClause = trimmedQuery[(orderByIndex + "ORDER BY".Length)..orderByEnd].Trim();
        }

        string? offsetLimitClause = null;
        if (offsetIndex >= 0)
        {
            offsetLimitClause = trimmedQuery[(offsetIndex + "OFFSET".Length)..].Trim();
        }

        var top = ParseTop(ref selectClause, parameters);
        var projection = ParseProjection(selectClause);
        var filters = ParseFilters(whereClause);
        var orderBy = ParseOrderBy(orderByClause);
        var (offset, limit) = ParseOffsetLimit(offsetLimitClause, parameters);

        return new QueryPlan(projection, filters, orderBy, top, offset, limit);
    }

    private static int? ParseTop(ref string selectClause, IReadOnlyDictionary<string, object?>? parameters)
    {
        var topMatch = Regex.Match(selectClause, @"^TOP\s+(?<value>@?[A-Za-z0-9_\-\.]+)\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
        if (!topMatch.Success)
        {
            return null;
        }

        selectClause = topMatch.Groups["rest"].Value.Trim();
        return ResolveNonNegativeInteger(ParseValueExpression(topMatch.Groups["value"].Value), parameters, "TOP");
    }

    private static Projection ParseProjection(string selectClause)
    {
        if (string.Equals(selectClause, "*", StringComparison.Ordinal))
        {
            return new Projection(ProjectionType.All, null, []);
        }

        if (string.Equals(selectClause, "COUNT(1)", StringComparison.OrdinalIgnoreCase))
        {
            return new Projection(ProjectionType.Count, null, []);
        }

        if (selectClause.StartsWith("VALUE", StringComparison.OrdinalIgnoreCase))
        {
            var valueExpression = selectClause["VALUE".Length..].Trim();
            return new Projection(ProjectionType.Value, NormalizePath(valueExpression), []);
        }

        var fields = SplitTopLevel(selectClause, ',')
            .Select(NormalizePath)
            .ToList();
        if (fields.Count == 0)
        {
            throw CosmosEmulatorException.BadRequest("SELECT must project at least one field.");
        }

        return new Projection(ProjectionType.Fields, null, fields);
    }

    private static IReadOnlyList<FilterClause> ParseFilters(string? whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
        {
            return [];
        }

        return SplitTopLevelKeyword(whereClause, "AND")
            .Select(ParseFilter)
            .ToList();
    }

    private static FilterClause ParseFilter(string clause)
    {
        var containsMatch = Regex.Match(clause, @"^CONTAINS\((?<path>[^,]+),(?<value>.+)\)$", RegexOptions.IgnoreCase);
        if (containsMatch.Success)
        {
            return new ContainsFilter(
                NormalizePath(containsMatch.Groups["path"].Value),
                ParseValueExpression(containsMatch.Groups["value"].Value));
        }

        var arrayContainsMatch = Regex.Match(clause, @"^ARRAY_CONTAINS\((?<path>[^,]+),(?<value>.+)\)$", RegexOptions.IgnoreCase);
        if (arrayContainsMatch.Success)
        {
            return new ArrayContainsFilter(
                NormalizePath(arrayContainsMatch.Groups["path"].Value),
                ParseValueExpression(arrayContainsMatch.Groups["value"].Value));
        }

        var isDefinedMatch = Regex.Match(clause, @"^IS_DEFINED\((?<path>.+)\)$", RegexOptions.IgnoreCase);
        if (isDefinedMatch.Success)
        {
            return new IsDefinedFilter(NormalizePath(isDefinedMatch.Groups["path"].Value));
        }

        var inMatch = Regex.Match(clause, @"^(?<path>c(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s+IN\s*\((?<values>.+)\)$", RegexOptions.IgnoreCase);
        if (inMatch.Success)
        {
            var values = SplitTopLevel(inMatch.Groups["values"].Value, ',')
                .Select(ParseValueExpression)
                .ToList();
            return new InFilter(NormalizePath(inMatch.Groups["path"].Value), values);
        }

        var comparisonMatch = Regex.Match(clause, @"^(?<path>c(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s*(?<operator>>=|<=|=|>|<)\s*(?<value>.+)$", RegexOptions.IgnoreCase);
        if (comparisonMatch.Success)
        {
            return new ComparisonFilter(
                NormalizePath(comparisonMatch.Groups["path"].Value),
                comparisonMatch.Groups["operator"].Value,
                ParseValueExpression(comparisonMatch.Groups["value"].Value));
        }

        throw CosmosEmulatorException.BadRequest($"Unsupported WHERE clause expression '{clause}'.");
    }

    private static OrderByClause? ParseOrderBy(string? orderByClause)
    {
        if (string.IsNullOrWhiteSpace(orderByClause))
        {
            return null;
        }

        var match = Regex.Match(orderByClause, @"^(?<path>c(?:\.[A-Za-z_][A-Za-z0-9_]*)+)(?:\s+(?<direction>ASC|DESC))?$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw CosmosEmulatorException.BadRequest("Unsupported ORDER BY clause.");
        }

        return new OrderByClause(
            NormalizePath(match.Groups["path"].Value),
            string.Equals(match.Groups["direction"].Value, "DESC", StringComparison.OrdinalIgnoreCase));
    }

    private static (int? offset, int? limit) ParseOffsetLimit(string? offsetLimitClause, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(offsetLimitClause))
        {
            return (null, null);
        }

        var match = Regex.Match(offsetLimitClause, @"^(?<offset>.+?)\s+LIMIT\s+(?<limit>.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw CosmosEmulatorException.BadRequest("OFFSET queries must include LIMIT.");
        }

        var offset = ResolveNonNegativeInteger(ParseValueExpression(match.Groups["offset"].Value), parameters, "OFFSET");
        var limit = ResolveNonNegativeInteger(ParseValueExpression(match.Groups["limit"].Value), parameters, "LIMIT");
        return (offset, limit);
    }

    private static List<DocumentContext> ApplyOrdering(List<DocumentContext> documents, QueryPlan plan)
    {
        IOrderedEnumerable<DocumentContext> orderedDocuments;
        if (plan.OrderBy is null)
        {
            orderedDocuments = documents
                .OrderBy(context => context.Document.Timestamp)
                .ThenBy(context => context.Document.Id, StringComparer.Ordinal);
        }
        else
        {
            Func<DocumentContext, object?> keySelector = context => ResolveComparablePathValue(context.ResponseBody, plan.OrderBy.Path);
            orderedDocuments = plan.OrderBy.Descending
                ? documents.OrderByDescending(keySelector, QueryValueComparer.Instance)
                : documents.OrderBy(keySelector, QueryValueComparer.Instance);

            orderedDocuments = orderedDocuments
                .ThenBy(context => context.Document.Id, StringComparer.Ordinal);
        }

        return orderedDocuments.ToList();
    }

    private static List<DocumentContext> ApplyWindowing(List<DocumentContext> documents, QueryPlan plan)
    {
        IEnumerable<DocumentContext> window = documents;
        if (plan.Top is int top)
        {
            window = window.Take(top);
        }

        if (plan.Offset is int offset)
        {
            window = window.Skip(offset);
        }

        if (plan.Limit is int limit)
        {
            window = window.Take(limit);
        }

        return window.ToList();
    }

    private static bool MatchesFilters(JsonObject document, IReadOnlyList<FilterClause> filters, IReadOnlyDictionary<string, object?>? parameters)
    {
        foreach (var filter in filters)
        {
            if (!MatchesFilter(document, filter, parameters))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesFilter(JsonObject document, FilterClause filter, IReadOnlyDictionary<string, object?>? parameters)
    {
        return filter switch
        {
            ComparisonFilter comparison => MatchesComparison(document, comparison, parameters),
            InFilter inFilter => MatchesIn(document, inFilter, parameters),
            ContainsFilter contains => MatchesContains(document, contains, parameters),
            ArrayContainsFilter arrayContains => MatchesArrayContains(document, arrayContains, parameters),
            IsDefinedFilter isDefined => TryResolvePath(document, isDefined.Path, out _),
            _ => throw CosmosEmulatorException.BadRequest("Unsupported filter expression.")
        };
    }

    private static bool MatchesComparison(JsonObject document, ComparisonFilter comparison, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = ResolveComparablePathValue(document, comparison.Path);
        var right = ResolveParameterOrLiteral(comparison.Value, parameters);
        if (ReferenceEquals(left, UndefinedValue))
        {
            return false;
        }

        return comparison.Operator switch
        {
            "=" => AreEqual(left, right),
            ">" => CompareValues(left, right) > 0,
            "<" => CompareValues(left, right) < 0,
            ">=" => CompareValues(left, right) >= 0,
            "<=" => CompareValues(left, right) <= 0,
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported operator '{comparison.Operator}'.")
        };
    }

    private static bool MatchesIn(JsonObject document, InFilter filter, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = ResolveComparablePathValue(document, filter.Path);
        if (ReferenceEquals(left, UndefinedValue))
        {
            return false;
        }

        return filter.Values.Any(value => AreEqual(left, ResolveParameterOrLiteral(value, parameters)));
    }

    private static bool MatchesContains(JsonObject document, ContainsFilter filter, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = ResolveComparablePathValue(document, filter.Path);
        var right = ResolveParameterOrLiteral(filter.Value, parameters);
        return left is string input && right is string search && input.Contains(search, StringComparison.Ordinal);
    }

    private static bool MatchesArrayContains(JsonObject document, ArrayContainsFilter filter, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (!TryResolvePath(document, filter.Path, out var arrayNode) || arrayNode is not JsonArray array)
        {
            return false;
        }

        var expected = ResolveParameterOrLiteral(filter.Value, parameters);
        foreach (var item in array)
        {
            if (AreEqual(NormalizeRuntimeValue(item), expected))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject Project(JsonObject document, Projection projection)
    {
        return projection.Type switch
        {
            ProjectionType.All => document.DeepClone().AsObject(),
            ProjectionType.Fields => ProjectFields(document, projection.Fields),
            ProjectionType.Value => ProjectValue(document, projection.ValuePath!),
            _ => throw CosmosEmulatorException.BadRequest("Unsupported projection.")
        };
    }

    private static JsonObject ProjectFields(JsonObject document, IReadOnlyList<string> fields)
    {
        var projected = new JsonObject();
        foreach (var field in fields)
        {
            if (TryResolvePath(document, field, out var value))
            {
                projected[GetPropertyAlias(field)] = CloneNode(value);
            }
        }

        return projected;
    }

    private static JsonObject ProjectValue(JsonObject document, string path)
    {
        var projected = new JsonObject();
        projected["$1"] = TryResolvePath(document, path, out var value) ? CloneNode(value) : null;
        return projected;
    }

    private static object? ResolveComparablePathValue(JsonObject document, string path)
    {
        return TryResolvePath(document, path, out var value)
            ? NormalizeRuntimeValue(value)
            : UndefinedValue;
    }

    private static object? ResolveParameterOrLiteral(ValueExpression expression, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (!expression.IsParameter)
        {
            return expression.Value;
        }

        if (parameters is null)
        {
            throw CosmosEmulatorException.BadRequest($"Missing query parameter '{expression.ParameterName}'.");
        }

        if (!parameters.TryGetValue(expression.ParameterName!, out var value)
            && !parameters.TryGetValue(expression.ParameterName!.TrimStart('@'), out value))
        {
            throw CosmosEmulatorException.BadRequest($"Missing query parameter '{expression.ParameterName}'.");
        }

        return NormalizeRuntimeValue(value);
    }

    private static ValueExpression ParseValueExpression(string token)
    {
        var trimmedToken = token.Trim();
        if (trimmedToken.StartsWith("@", StringComparison.Ordinal))
        {
            return new ValueExpression(true, trimmedToken, null);
        }

        if (trimmedToken.Length >= 2 && trimmedToken[0] == '\'' && trimmedToken[^1] == '\'')
        {
            return new ValueExpression(false, null, trimmedToken[1..^1].Replace("''", "'", StringComparison.Ordinal));
        }

        if (bool.TryParse(trimmedToken, out var boolValue))
        {
            return new ValueExpression(false, null, boolValue);
        }

        if (string.Equals(trimmedToken, "null", StringComparison.OrdinalIgnoreCase))
        {
            return new ValueExpression(false, null, null);
        }

        if (double.TryParse(trimmedToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue))
        {
            return new ValueExpression(false, null, numericValue);
        }

        throw CosmosEmulatorException.BadRequest($"Unsupported query value '{trimmedToken}'.");
    }

    private static int ResolveNonNegativeInteger(ValueExpression expression, IReadOnlyDictionary<string, object?>? parameters, string clauseName)
    {
        var resolved = ResolveParameterOrLiteral(expression, parameters);
        if (!TryConvertToInt32(resolved, out var value) || value < 0)
        {
            throw CosmosEmulatorException.BadRequest($"{clauseName} requires a non-negative integer value.");
        }

        return value;
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

    private static string NormalizePath(string rawPath)
    {
        var trimmedPath = rawPath.Trim();
        if (!trimmedPath.StartsWith("c.", StringComparison.OrdinalIgnoreCase))
        {
            throw CosmosEmulatorException.BadRequest($"Unsupported property path '{trimmedPath}'.");
        }

        var normalized = trimmedPath[2..].Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw CosmosEmulatorException.BadRequest($"Unsupported property path '{trimmedPath}'.");
        }

        return normalized;
    }

    private static bool TryResolvePath(JsonObject document, string path, out JsonNode? value)
    {
        JsonNode? current = document;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (current)
            {
                case JsonObject currentObject when currentObject.TryGetPropertyValue(segment, out var propertyValue):
                    current = propertyValue;
                    break;
                case JsonArray currentArray when int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                                                 && index >= 0
                                                 && index < currentArray.Count:
                    current = currentArray[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        value = current;
        return true;
    }

    private static object? NormalizeRuntimeValue(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => NormalizeJsonNode(node),
            JsonElement element => NormalizeJsonElement(element),
            byte number => (double)number,
            sbyte number => (double)number,
            short number => (double)number,
            ushort number => (double)number,
            int number => (double)number,
            uint number => number,
            long number => (double)number,
            ulong number => (double)number,
            float number => (double)number,
            double number => number,
            decimal number => (double)number,
            _ => value
        };
    }

    private static object? NormalizeJsonNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => JsonSerializer.Deserialize<double>(node.ToJsonString()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => node.DeepClone().AsObject(),
            JsonValueKind.Array => node.DeepClone().AsArray(),
            _ => node.ToJsonString()
        };
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => JsonNode.Parse(element.GetRawText())?.AsObject(),
            JsonValueKind.Array => JsonNode.Parse(element.GetRawText())?.AsArray(),
            _ => element.ToString()
        };
    }

    private static bool AreEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, UndefinedValue) || ReferenceEquals(right, UndefinedValue))
        {
            return false;
        }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            return JsonNode.DeepEquals(leftObject, rightObject);
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            return JsonNode.DeepEquals(leftArray, rightArray);
        }

        if (TryConvertToDouble(left, out var leftNumber) && TryConvertToDouble(right, out var rightNumber))
        {
            return leftNumber.Equals(rightNumber);
        }

        return Equals(left, right);
    }

    private static int CompareValues(object? left, object? right)
    {
        if (ReferenceEquals(left, UndefinedValue) || ReferenceEquals(right, UndefinedValue))
        {
            return -1;
        }

        if (TryConvertToDouble(left, out var leftNumber) && TryConvertToDouble(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (left is string leftString && right is string rightString)
        {
            return string.Compare(leftString, rightString, StringComparison.Ordinal);
        }

        if (left is bool leftBool && right is bool rightBool)
        {
            return leftBool.CompareTo(rightBool);
        }

        throw CosmosEmulatorException.BadRequest("Comparison operators require compatible scalar values.");
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        switch (value)
        {
            case byte byteValue:
                result = byteValue;
                return true;
            case sbyte sbyteValue:
                result = sbyteValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case ushort ushortValue:
                result = ushortValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case ulong ulongValue:
                result = ulongValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case double doubleValue:
                result = doubleValue;
                return true;
            case decimal decimalValue:
                result = (double)decimalValue;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool TryConvertToInt32(object? value, out int result)
    {
        switch (value)
        {
            case byte byteValue:
                result = byteValue;
                return true;
            case sbyte sbyteValue:
                result = sbyteValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case ushort ushortValue:
                result = ushortValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                result = (int)longValue;
                return true;
            case float floatValue when Math.Abs(floatValue % 1) < float.Epsilon && floatValue is >= int.MinValue and <= int.MaxValue:
                result = (int)floatValue;
                return true;
            case double doubleValue when Math.Abs(doubleValue % 1) < double.Epsilon && doubleValue is >= int.MinValue and <= int.MaxValue:
                result = (int)doubleValue;
                return true;
            case decimal decimalValue when decimalValue == decimal.Truncate(decimalValue) && decimalValue is >= int.MinValue and <= int.MaxValue:
                result = (int)decimalValue;
                return true;
            case string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node?.DeepClone();
    }

    private static string GetPropertyAlias(string path)
    {
        var lastDot = path.LastIndexOf('.');
        return lastDot >= 0 ? path[(lastDot + 1)..] : path;
    }

    private static int FindTopLevelKeyword(string text, string keyword, int startIndex)
    {
        var depth = 0;
        var inString = false;

        for (var index = Math.Max(startIndex, 0); index <= text.Length - keyword.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (current == '\'')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        index++;
                    }
                    else
                    {
                        inString = false;
                    }
                }

                continue;
            }

            if (current == '\'')
            {
                inString = true;
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (IsKeywordAt(text, index, keyword))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (!text.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var before = index - 1;
        if (before >= 0 && !char.IsWhiteSpace(text[before]))
        {
            return false;
        }

        var after = index + keyword.Length;
        if (after < text.Length && !char.IsWhiteSpace(text[after]))
        {
            return false;
        }

        return true;
    }

    private static List<string> SplitTopLevel(string input, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var index = 0; index < input.Length; index++)
        {
            var current = input[index];
            if (inString)
            {
                if (current == '\'' && (index + 1 >= input.Length || input[index + 1] != '\''))
                {
                    inString = false;
                }
                else if (current == '\'' && index + 1 < input.Length && input[index + 1] == '\'')
                {
                    index++;
                }

                continue;
            }

            switch (current)
            {
                case '\'':
                    inString = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                default:
                    if (depth == 0 && current == separator)
                    {
                        parts.Add(input[start..index].Trim());
                        start = index + 1;
                    }

                    break;
            }
        }

        parts.Add(input[start..].Trim());
        return parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
    }

    private static List<string> SplitTopLevelKeyword(string input, string keyword)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var index = 0; index <= input.Length - keyword.Length; index++)
        {
            var current = input[index];
            if (inString)
            {
                if (current == '\'')
                {
                    if (index + 1 < input.Length && input[index + 1] == '\'')
                    {
                        index++;
                    }
                    else
                    {
                        inString = false;
                    }
                }

                continue;
            }

            if (current == '\'')
            {
                inString = true;
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0 && IsKeywordAt(input, index, keyword))
            {
                parts.Add(input[start..index].Trim());
                start = index + keyword.Length;
                index = start - 1;
            }
        }

        parts.Add(input[start..].Trim());
        return parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
    }

    private sealed record DocumentContext(CosmosDocument Document, JsonObject ResponseBody);

    private sealed record QueryPlan(
        Projection Projection,
        IReadOnlyList<FilterClause> Filters,
        OrderByClause? OrderBy,
        int? Top,
        int? Offset,
        int? Limit)
    {
        public ProjectionType ProjectionType => Projection.Type;
    }

    private sealed record Projection(ProjectionType Type, string? ValuePath, IReadOnlyList<string> Fields);

    private enum ProjectionType
    {
        All,
        Fields,
        Value,
        Count
    }

    private abstract record FilterClause(string Path);

    private sealed record ComparisonFilter(string Path, string Operator, ValueExpression Value) : FilterClause(Path);

    private sealed record InFilter(string Path, IReadOnlyList<ValueExpression> Values) : FilterClause(Path);

    private sealed record ContainsFilter(string Path, ValueExpression Value) : FilterClause(Path);

    private sealed record ArrayContainsFilter(string Path, ValueExpression Value) : FilterClause(Path);

    private sealed record IsDefinedFilter(string Path) : FilterClause(Path);

    private sealed record OrderByClause(string Path, bool Descending);

    private sealed record ValueExpression(bool IsParameter, string? ParameterName, object? Value);

    private sealed class QueryValueComparer : IComparer<object?>
    {
        public static QueryValueComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (ReferenceEquals(x, UndefinedValue))
            {
                return -1;
            }

            if (ReferenceEquals(y, UndefinedValue))
            {
                return 1;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            if (TryConvertToDouble(x, out var xNumber) && TryConvertToDouble(y, out var yNumber))
            {
                return xNumber.CompareTo(yNumber);
            }

            if (x is string xString && y is string yString)
            {
                return string.Compare(xString, yString, StringComparison.Ordinal);
            }

            if (x is bool xBool && y is bool yBool)
            {
                return xBool.CompareTo(yBool);
            }

            return string.Compare(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
    }
}
