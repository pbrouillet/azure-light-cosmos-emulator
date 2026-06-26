using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.NoSql.Query;

public sealed class QueryExplainService(IDocumentStore documentStore)
{
    private static readonly string[] AggregateFunctionNames = ["COUNT", "SUM", "AVG", "MIN", "MAX"];

    public async Task<QueryExplainResult> ExplainAsync(
        string databaseId,
        string containerId,
        string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(databaseId))
        {
            throw CosmosEmulatorException.BadRequest("databaseId is required.");
        }

        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw CosmosEmulatorException.BadRequest("containerId is required.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw CosmosEmulatorException.BadRequest("query is required.");
        }

        var normalizedQuery = query.Trim().TrimEnd(';').Trim();
        var container = await documentStore.GetContainerAsync(databaseId, containerId, ct);
        var parsedQuery = SqlParser.Parse(normalizedQuery);
        var analysis = AnalyzeIndexes(parsedQuery, container);

        return new QueryExplainResult
        {
            Query = normalizedQuery,
            QueryPlan = BuildQueryPlan(parsedQuery),
            EstimatedRuCharge = EstimateRu(parsedQuery),
            IndexAnalysis = new QueryExplainIndexAnalysis
            {
                UsedIndexes = analysis.UsedIndexes,
                Recommendations = analysis.Recommendations,
                IndexingPolicyPaths = new QueryExplainIndexingPolicyPaths
                {
                    Included = container.IndexingPolicy.IncludedPaths.Select(path => path.Path).ToArray(),
                    Excluded = container.IndexingPolicy.ExcludedPaths.Select(path => path.Path).ToArray()
                }
            },
            Warnings = BuildWarnings(parsedQuery, container, analysis),
            EducationalNotes = BuildEducationalNotes(parsedQuery, analysis)
        };
    }

    private static JsonObject BuildQueryPlan(ParsedQuery parsedQuery)
    {
        var plan = new JsonObject
        {
            ["type"] = "select",
            ["projections"] = ToJsonArray(parsedQuery.Projections.Select(FormatScalarExpression)),
            ["source"] = parsedQuery.FromAlias
        };

        if (parsedQuery.Joins.Count == 1)
        {
            var join = parsedQuery.Joins[0];
            plan["join"] = new JsonObject
            {
                ["alias"] = join.Alias,
                ["path"] = FormatScalarExpression(join.SourceExpression)
            };
        }
        else if (parsedQuery.Joins.Count > 1)
        {
            plan["joins"] = new JsonArray(parsedQuery.Joins.Select(join => (JsonNode?)new JsonObject
            {
                ["alias"] = join.Alias,
                ["path"] = FormatScalarExpression(join.SourceExpression)
            }).ToArray());
        }

        if (parsedQuery.Where is not null)
        {
            plan["filters"] = new JsonArray(ConvertBooleanExpression(parsedQuery.Where));
        }

        if (parsedQuery.GroupBy.Count > 0)
        {
            plan["groupBy"] = ToJsonArray(parsedQuery.GroupBy.Select(FormatScalarExpression));
        }

        if (parsedQuery.OrderBy.Count > 0)
        {
            plan["orderBy"] = new JsonArray(parsedQuery.OrderBy.Select(orderBy => (JsonNode?)new JsonObject
            {
                ["field"] = FormatScalarExpression(orderBy.Expression),
                ["direction"] = orderBy.Descending ? "DESC" : "ASC"
            }).ToArray());
        }

        if (parsedQuery.Aggregates.Count > 0)
        {
            plan["aggregates"] = ToJsonArray(parsedQuery.Aggregates);
        }

        return plan;
    }

    private static QueryExplainRuCharge EstimateRu(ParsedQuery parsedQuery)
    {
        const double baseCost = 2.5;
        var predicateCount = CountPredicates(parsedQuery.Where);
        var filterCost = predicateCount == 0
            ? 0
            : 0.5 + Math.Max(0, predicateCount - 1) * 0.25;

        if (ContainsOperator(parsedQuery.Where, BooleanOperator.Or))
        {
            filterCost += 0.25;
        }

        if (ContainsFunction(parsedQuery.Where))
        {
            filterCost += 0.25;
        }

        var joinCost = parsedQuery.Joins.Count * 2.0;
        var aggregateCost = parsedQuery.Aggregates.Count > 0 || parsedQuery.GroupBy.Count > 0 ? 0.5 : 0;
        var orderByCost = parsedQuery.OrderBy.Count > 0 ? 0.5 + Math.Max(0, parsedQuery.OrderBy.Count - 1) * 0.25 : 0;
        const double crossPartitionMultiplier = 1;
        var total = Math.Round((baseCost + filterCost + joinCost + aggregateCost + orderByCost) * crossPartitionMultiplier, 1);

        return new QueryExplainRuCharge
        {
            Base = Math.Round(baseCost, 1),
            FilterCost = Math.Round(filterCost, 1),
            JoinCost = Math.Round(joinCost, 1),
            AggregateCost = Math.Round(aggregateCost, 1),
            OrderByCost = Math.Round(orderByCost, 1),
            CrossPartitionMultiplier = crossPartitionMultiplier,
            Total = total
        };
    }

    private static IndexAnalysisContext AnalyzeIndexes(ParsedQuery parsedQuery, CosmosContainer container)
    {
        var recommendations = new HashSet<string>(StringComparer.Ordinal);
        var usedIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var comparisonUsages = GetComparisonUsages(parsedQuery.Where).ToList();
        var functionUsages = GetFunctionUsages(parsedQuery.Where).ToList();
        var orderByPaths = parsedQuery.OrderBy
            .Select(orderBy => GetIndexPath(orderBy.Expression))
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var usage in comparisonUsages)
        {
            var indexPath = GetIndexPath(usage.Path);
            if (indexPath is null)
            {
                continue;
            }

            if (!ContainsOperator(parsedQuery.Where, BooleanOperator.Or) && IsIndexed(indexPath, container.IndexingPolicy))
            {
                usedIndexes.Add(indexPath);
            }

            if (usage.Operator is ComparisonOperator.GreaterThan
                or ComparisonOperator.GreaterThanOrEqual
                or ComparisonOperator.LessThan
                or ComparisonOperator.LessThanOrEqual)
            {
                recommendations.Add($"The filter on '{GetFieldName(usage.Path.Path)}' would benefit from a range index on {indexPath}");
            }
            else if (!IsIndexed(indexPath, container.IndexingPolicy))
            {
                recommendations.Add($"Consider indexing {indexPath} to speed up filters on '{GetFieldName(usage.Path.Path)}'");
            }
        }

        foreach (var usage in functionUsages)
        {
            if (usage.Path is null)
            {
                continue;
            }

            var indexPath = GetIndexPath(usage.Path);
            if (indexPath is null)
            {
                continue;
            }

            if (!ContainsOperator(parsedQuery.Where, BooleanOperator.Or) && IsIndexed(indexPath, container.IndexingPolicy))
            {
                usedIndexes.Add(indexPath);
            }

            if (usage.Name.Equals("STARTSWITH", StringComparison.OrdinalIgnoreCase))
            {
                recommendations.Add($"STARTSWITH on '{GetFieldName(usage.Path.Path)}' can use a range index on {indexPath} if available");
            }
            else if (usage.Name.Equals("CONTAINS", StringComparison.OrdinalIgnoreCase))
            {
                recommendations.Add($"CONTAINS on '{GetFieldName(usage.Path.Path)}' may still require scanning matching index ranges");
            }
        }

        foreach (var orderBy in parsedQuery.OrderBy)
        {
            var indexPath = GetIndexPath(orderBy.Expression);
            if (indexPath is null)
            {
                continue;
            }

            if (IsIndexed(indexPath, container.IndexingPolicy))
            {
                usedIndexes.Add(indexPath);
            }

            if (!HasCompositeIndex(container.IndexingPolicy, indexPath, orderBy.Descending))
            {
                recommendations.Add($"Consider adding a composite index on ({GetFieldName(FormatScalarExpression(orderBy.Expression))} {(orderBy.Descending ? "DESC" : "ASC")}) to optimize ORDER BY");
            }
        }

        if (parsedQuery.Joins.Count > 0)
        {
            recommendations.Add("JOIN operations expand arrays and increase RU cost proportionally to array size");
        }

        return new IndexAnalysisContext(
            usedIndexes.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            recommendations.ToArray());
    }

    private static string[] BuildWarnings(ParsedQuery parsedQuery, CosmosContainer container, IndexAnalysisContext analysis)
    {
        var warnings = new List<string>();
        if (container.PartitionKey.Paths.Count > 0 && !TargetsPartitionKey(parsedQuery.Where, container.PartitionKey.Paths))
        {
            warnings.Add("Cross-partition query detected when no partition key is specified — this scans all partitions");
        }

        if (parsedQuery.GroupBy.Count > 0)
        {
            warnings.Add("GROUP BY requires the query engine to buffer all results before returning");
        }

        if (parsedQuery.OrderBy.Count > 0 && analysis.UsedIndexes.Length == 0)
        {
            warnings.Add("ORDER BY on a non-indexed field can require an in-memory sort and increase RU cost");
        }

        return warnings.ToArray();
    }

    private static string[] BuildEducationalNotes(ParsedQuery parsedQuery, IndexAnalysisContext analysis)
    {
        var notes = new List<string>();
        if (parsedQuery.Joins.Count > 0)
        {
            notes.Add($"This query uses an intra-document JOIN which expands the '{GetFieldName(FormatScalarExpression(parsedQuery.Joins[0].SourceExpression))}' array — each item produces a separate result row");
        }

        if (ContainsOperator(parsedQuery.Where, BooleanOperator.Or))
        {
            notes.Add("The OR operator in the WHERE clause may prevent efficient index usage in the real service");
        }

        if (parsedQuery.Aggregates.Count > 0)
        {
            notes.Add("Aggregate queries (COUNT, SUM, AVG, MIN, MAX) are computed server-side and return one result per group or per query");
        }

        if (parsedQuery.OrderBy.Count > 0)
        {
            notes.Add(analysis.UsedIndexes.Length > 0
                ? "ORDER BY can stream results more efficiently when the sort path is indexed"
                : "ORDER BY on a non-indexed field requires an in-memory sort which increases RU cost");
        }

        if (notes.Count == 0)
        {
            notes.Add("Simple SELECT queries are usually dominated by document reads and the number of projected fields.");
        }

        return notes.ToArray();
    }

    private static bool TargetsPartitionKey(BooleanExpression? expression, IReadOnlyList<string> partitionKeyPaths)
    {
        if (expression is null)
        {
            return false;
        }

        var normalizedPartitionKeys = partitionKeyPaths
            .Select(path => path.Trim())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return GetComparisonUsages(expression)
            .Select(usage => GetIndexPath(usage.Path))
            .Where(static path => path is not null)
            .Cast<string>()
            .Any(normalizedPartitionKeys.Contains);
    }

    private static IEnumerable<ComparisonUsage> GetComparisonUsages(BooleanExpression? expression)
    {
        if (expression is null)
        {
            yield break;
        }

        switch (expression)
        {
            case BinaryBooleanExpression binary:
                foreach (var usage in GetComparisonUsages(binary.Left))
                {
                    yield return usage;
                }

                foreach (var usage in GetComparisonUsages(binary.Right))
                {
                    yield return usage;
                }

                yield break;
            case NotBooleanExpression not:
                foreach (var usage in GetComparisonUsages(not.Expression))
                {
                    yield return usage;
                }

                yield break;
            case ComparisonBooleanExpression comparison when comparison.Left is PathExpression path:
                yield return new ComparisonUsage(path, comparison.Operator);
                yield break;
            case InBooleanExpression inExpression when inExpression.Left is PathExpression path:
                yield return new ComparisonUsage(path, ComparisonOperator.Equal);
                yield break;
        }
    }

    private static IEnumerable<FunctionUsage> GetFunctionUsages(BooleanExpression? expression)
    {
        if (expression is null)
        {
            yield break;
        }

        switch (expression)
        {
            case BinaryBooleanExpression binary:
                foreach (var usage in GetFunctionUsages(binary.Left))
                {
                    yield return usage;
                }

                foreach (var usage in GetFunctionUsages(binary.Right))
                {
                    yield return usage;
                }

                yield break;
            case NotBooleanExpression not:
                foreach (var usage in GetFunctionUsages(not.Expression))
                {
                    yield return usage;
                }

                yield break;
            case ComparisonBooleanExpression comparison:
                foreach (var usage in GetFunctionUsages(comparison.Left))
                {
                    yield return usage;
                }

                foreach (var usage in GetFunctionUsages(comparison.Right))
                {
                    yield return usage;
                }

                yield break;
            case InBooleanExpression inExpression:
                foreach (var usage in GetFunctionUsages(inExpression.Left))
                {
                    yield return usage;
                }

                foreach (var value in inExpression.Values)
                {
                    foreach (var usage in GetFunctionUsages(value))
                    {
                        yield return usage;
                    }
                }

                yield break;
            case ScalarBooleanExpression scalar:
                foreach (var usage in GetFunctionUsages(scalar.Expression))
                {
                    yield return usage;
                }

                yield break;
        }
    }

    private static IEnumerable<FunctionUsage> GetFunctionUsages(ScalarExpression expression)
    {
        switch (expression)
        {
            case FunctionCallExpression function:
            {
                var functionPath = function.Arguments.OfType<PathExpression>().FirstOrDefault();
                yield return new FunctionUsage(function.Name, functionPath);
                foreach (var argument in function.Arguments)
                {
                    foreach (var usage in GetFunctionUsages(argument))
                    {
                        yield return usage;
                    }
                }

                yield break;
            }
            default:
                yield break;
        }
    }

    private static int CountPredicates(BooleanExpression? expression)
    {
        return expression switch
        {
            null => 0,
            BinaryBooleanExpression binary => CountPredicates(binary.Left) + CountPredicates(binary.Right),
            NotBooleanExpression not => CountPredicates(not.Expression),
            ComparisonBooleanExpression or InBooleanExpression or ScalarBooleanExpression => 1,
            _ => 0
        };
    }

    private static bool ContainsOperator(BooleanExpression? expression, BooleanOperator @operator)
    {
        return expression switch
        {
            BinaryBooleanExpression binary => binary.Operator == @operator
                || ContainsOperator(binary.Left, @operator)
                || ContainsOperator(binary.Right, @operator),
            NotBooleanExpression not => ContainsOperator(not.Expression, @operator),
            _ => false
        };
    }

    private static bool ContainsFunction(BooleanExpression? expression)
    {
        return GetFunctionUsages(expression).Any();
    }

    private static JsonObject ConvertBooleanExpression(BooleanExpression expression)
    {
        return expression switch
        {
            BinaryBooleanExpression binary => new JsonObject
            {
                ["type"] = binary.Operator == BooleanOperator.Or ? "or" : "and",
                ["left"] = ConvertBooleanExpression(binary.Left),
                ["right"] = ConvertBooleanExpression(binary.Right)
            },
            ComparisonBooleanExpression comparison => new JsonObject
            {
                ["type"] = "comparison",
                ["field"] = FormatScalarExpression(comparison.Left),
                ["operator"] = ToComparisonOperator(comparison.Operator),
                ["value"] = ConvertScalarValue(comparison.Right)
            },
            InBooleanExpression inExpression => new JsonObject
            {
                ["type"] = inExpression.Negated ? "notIn" : "in",
                ["field"] = FormatScalarExpression(inExpression.Left),
                ["values"] = new JsonArray(inExpression.Values.Select(value => ConvertScalarValue(value)).ToArray())
            },
            NotBooleanExpression not => new JsonObject
            {
                ["type"] = "not",
                ["expression"] = ConvertBooleanExpression(not.Expression)
            },
            ScalarBooleanExpression { Expression: FunctionCallExpression function } => new JsonObject
            {
                ["type"] = "function",
                ["name"] = function.Name.ToUpperInvariant(),
                ["args"] = ToJsonArray(function.Arguments.Select(FormatScalarExpression))
            },
            ScalarBooleanExpression scalar => new JsonObject
            {
                ["type"] = "scalar",
                ["expression"] = FormatScalarExpression(scalar.Expression)
            },
            _ => new JsonObject
            {
                ["type"] = "unknown"
            }
        };
    }

    private static JsonNode? ConvertScalarValue(ScalarExpression expression)
    {
        return expression switch
        {
            LiteralExpression literal => CreateJsonValue(literal.Value),
            _ => JsonValue.Create(FormatScalarExpression(expression))
        };
    }

    private static JsonNode? CreateJsonValue(object? value)
    {
        return value switch
        {
            null => null,
            string stringValue => JsonValue.Create(stringValue),
            bool boolValue => JsonValue.Create(boolValue),
            int intValue => JsonValue.Create(intValue),
            long longValue => JsonValue.Create(longValue),
            double doubleValue => JsonValue.Create(doubleValue),
            decimal decimalValue => JsonValue.Create(decimalValue),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        return new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    private static string FormatScalarExpression(ScalarExpression expression)
    {
        return expression switch
        {
            LiteralExpression literal => FormatLiteral(literal.Value),
            ParameterExpression parameter => parameter.Name,
            PathExpression path => path.Path,
            FunctionCallExpression function => $"{function.Name.ToUpperInvariant()}({string.Join(", ", function.Arguments.Select(FormatScalarExpression))})",
            StarExpression => "*",
            ScalarSubqueryExpression subquery => $"({subquery.InnerQuery})",
            ArrayLiteralExpression arrayLit => $"[{string.Join(", ", arrayLit.Elements.Select(FormatScalarExpression))}]",
            ObjectLiteralExpression objectLit => $"{{{string.Join(", ", objectLit.Properties.Select(p => $"'{p.Key.Replace("'", "''", StringComparison.Ordinal)}': {FormatScalarExpression(p.Value)}"))}}}",
            _ => expression.ToString() ?? string.Empty
        };
    }

    private static string FormatLiteral(object? value)
    {
        return value switch
        {
            null => "null",
            string stringValue => $"'{stringValue.Replace("'", "''", StringComparison.Ordinal)}'",
            bool boolValue => boolValue ? "true" : "false",
            double doubleValue when doubleValue % 1 == 0 => doubleValue.ToString("0", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? string.Empty
        };
    }

    private static string ToComparisonOperator(ComparisonOperator comparisonOperator)
    {
        return comparisonOperator switch
        {
            ComparisonOperator.Equal => "=",
            ComparisonOperator.NotEqual => "!=",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.LessThan => "<",
            ComparisonOperator.GreaterThanOrEqual => ">=",
            ComparisonOperator.LessThanOrEqual => "<=",
            _ => "?"
        };
    }

    private static string? GetIndexPath(ScalarExpression expression)
    {
        return expression is PathExpression path ? GetIndexPath(path) : null;
    }

    private static string? GetIndexPath(PathExpression pathExpression)
    {
        var path = pathExpression.Path.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith('/'))
        {
            return path;
        }

        var dotIndex = path.IndexOf('.');
        if (dotIndex >= 0 && dotIndex < path.Length - 1)
        {
            path = path[(dotIndex + 1)..];
        }

        return "/" + path.Replace('.', '/');
    }

    private static bool IsIndexed(string path, IndexingPolicy indexingPolicy)
    {
        if (indexingPolicy.IndexingMode == IndexingMode.None)
        {
            return false;
        }

        if (indexingPolicy.ExcludedPaths.Any(excluded => PathMatches(excluded.Path, path)))
        {
            return false;
        }

        return indexingPolicy.IncludedPaths.Count == 0
            || indexingPolicy.IncludedPaths.Any(included => PathMatches(included.Path, path));
    }

    private static bool PathMatches(string configuredPath, string candidatePath)
    {
        var normalizedConfigured = NormalizePolicyPath(configuredPath);
        var normalizedCandidate = NormalizePolicyPath(candidatePath);

        return normalizedConfigured.Equals("/*", StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.Equals(normalizedConfigured, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedConfigured.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePolicyPath(string path)
    {
        return path
            .Replace("/?", string.Empty, StringComparison.Ordinal)
            .Replace("/*", "/*", StringComparison.Ordinal)
            .Replace('"', ' ')
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static bool HasCompositeIndex(IndexingPolicy indexingPolicy, string path, bool descending)
    {
        return indexingPolicy.CompositeIndexes?.Any(index =>
            index.Paths.Count > 0 && index.Paths[0].Path.Equals(path, StringComparison.OrdinalIgnoreCase)
            && index.Paths[0].Order == (descending ? SortOrder.Descending : SortOrder.Ascending)) == true;
    }

    private static string GetFieldName(string expression)
    {
        var normalized = expression.Trim().Trim('\'', '"');
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            return normalized[(slashIndex + 1)..];
        }

        var dotIndex = normalized.LastIndexOf('.');
        return dotIndex >= 0 && dotIndex < normalized.Length - 1
            ? normalized[(dotIndex + 1)..]
            : normalized;
    }

    private sealed record IndexAnalysisContext(string[] UsedIndexes, string[] Recommendations);

    private sealed record ComparisonUsage(PathExpression Path, ComparisonOperator Operator);

    private sealed record FunctionUsage(string Name, PathExpression? Path);

    private sealed record ParsedQuery(
        string FromAlias,
        IReadOnlyList<ScalarExpression> Projections,
        IReadOnlyList<string> Aggregates,
        IReadOnlyList<JoinClause> Joins,
        BooleanExpression? Where,
        IReadOnlyList<ScalarExpression> GroupBy,
        IReadOnlyList<OrderByClause> OrderBy);

    private sealed record JoinClause(string Alias, ScalarExpression SourceExpression);

    private sealed record OrderByClause(ScalarExpression Expression, bool Descending);

    private abstract record BooleanExpression;

    private sealed record BinaryBooleanExpression(BooleanExpression Left, BooleanOperator Operator, BooleanExpression Right) : BooleanExpression;

    private sealed record NotBooleanExpression(BooleanExpression Expression) : BooleanExpression;

    private sealed record ComparisonBooleanExpression(ScalarExpression Left, ComparisonOperator Operator, ScalarExpression Right) : BooleanExpression;

    private sealed record InBooleanExpression(ScalarExpression Left, IReadOnlyList<ScalarExpression> Values, bool Negated) : BooleanExpression;

    private sealed record ScalarBooleanExpression(ScalarExpression Expression) : BooleanExpression;

    private enum BooleanOperator
    {
        And,
        Or
    }

    private enum ComparisonOperator
    {
        Equal,
        NotEqual,
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual
    }

    private abstract record ScalarExpression;

    private sealed record LiteralExpression(object? Value) : ScalarExpression;

    private sealed record ParameterExpression(string Name) : ScalarExpression;

    private sealed record PathExpression(string Path) : ScalarExpression;

    private sealed record FunctionCallExpression(string Name, IReadOnlyList<ScalarExpression> Arguments) : ScalarExpression;

    private sealed record StarExpression : ScalarExpression;

    private sealed record ScalarSubqueryExpression(string InnerQuery) : ScalarExpression;

    private sealed record ArrayLiteralExpression(IReadOnlyList<ScalarExpression> Elements) : ScalarExpression;

    private sealed record ObjectLiteralExpression(IReadOnlyList<KeyValuePair<string, ScalarExpression>> Properties) : ScalarExpression;

    private static class SqlParser
    {
        public static ParsedQuery Parse(string query)
        {
            query = CosmosQueryEngine.StripSqlComments(query.Trim()).TrimEnd(';').Trim();
            if (!query.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                throw CosmosEmulatorException.BadRequest("Only SELECT queries are supported.");
            }

            var fromIndex = FindTopLevelKeyword(query, "FROM", 0);
            if (fromIndex < 0)
            {
                throw CosmosEmulatorException.BadRequest("Queries must include a FROM clause.");
            }

            var selectClause = query["SELECT".Length..fromIndex].Trim();
            var whereIndex = FindTopLevelKeyword(query, "WHERE", fromIndex + "FROM".Length);
            var groupByIndex = FindTopLevelKeyword(query, "GROUP BY", fromIndex + "FROM".Length);
            var orderByIndex = FindTopLevelKeyword(query, "ORDER BY", fromIndex + "FROM".Length);
            var offsetIndex = FindTopLevelKeyword(query, "OFFSET", fromIndex + "FROM".Length);
            var clauseIndexes = new[] { whereIndex, groupByIndex, orderByIndex, offsetIndex }
                .Where(index => index >= 0)
                .OrderBy(index => index)
                .ToArray();

            var fromClauseEnd = clauseIndexes.FirstOrDefault(query.Length);
            var fromClause = query[(fromIndex + "FROM".Length)..fromClauseEnd].Trim();
            var (fromAlias, joins) = ParseFromClause(fromClause);

            string? whereClause = null;
            if (whereIndex >= 0)
            {
                var whereEnd = new[] { groupByIndex, orderByIndex, offsetIndex }
                    .Where(index => index > whereIndex)
                    .OrderBy(index => index)
                    .FirstOrDefault(query.Length);
                whereClause = query[(whereIndex + "WHERE".Length)..whereEnd].Trim();
            }

            string? groupByClause = null;
            if (groupByIndex >= 0)
            {
                var groupByEnd = new[] { orderByIndex, offsetIndex }
                    .Where(index => index > groupByIndex)
                    .OrderBy(index => index)
                    .FirstOrDefault(query.Length);
                groupByClause = query[(groupByIndex + "GROUP BY".Length)..groupByEnd].Trim();
            }

            string? orderByClause = null;
            if (orderByIndex >= 0)
            {
                var orderByEnd = new[] { offsetIndex }
                    .Where(index => index > orderByIndex)
                    .OrderBy(index => index)
                    .FirstOrDefault(query.Length);
                orderByClause = query[(orderByIndex + "ORDER BY".Length)..orderByEnd].Trim();
            }

            var projections = ParseProjection(selectClause);
            var where = ParseWhere(whereClause);
            var groupBy = ParseGroupBy(groupByClause);
            var orderBy = ParseOrderBy(orderByClause);
            var aggregates = projections
                .Where(ContainsAggregateFunction)
                .Select(FormatScalarExpression)
                .ToArray();

            return new ParsedQuery(fromAlias, projections, aggregates, joins, where, groupBy, orderBy);
        }

        private static IReadOnlyList<ScalarExpression> ParseProjection(string selectClause)
        {
            if (string.Equals(selectClause, "*", StringComparison.Ordinal))
            {
                return [new StarExpression()];
            }

            if (selectClause.StartsWith("VALUE", StringComparison.OrdinalIgnoreCase))
            {
                return [ParseScalarExpression(selectClause["VALUE".Length..].Trim())];
            }

            var fields = SplitTopLevel(selectClause, ',')
                .Select(ParseScalarExpression)
                .ToArray();

            if (fields.Length == 0)
            {
                throw CosmosEmulatorException.BadRequest("SELECT must project at least one field.");
            }

            return fields;
        }

        private static BooleanExpression? ParseWhere(string? whereClause)
        {
            if (string.IsNullOrWhiteSpace(whereClause))
            {
                return null;
            }

            var parser = new ExpressionParser(whereClause);
            var expression = parser.ParseBooleanExpression();
            parser.ExpectEnd("WHERE");
            return expression;
        }

        private static IReadOnlyList<ScalarExpression> ParseGroupBy(string? groupByClause)
        {
            if (string.IsNullOrWhiteSpace(groupByClause))
            {
                return [];
            }

            return SplitTopLevel(groupByClause, ',')
                .Select(ParseScalarExpression)
                .ToArray();
        }

        private static IReadOnlyList<OrderByClause> ParseOrderBy(string? orderByClause)
        {
            if (string.IsNullOrWhiteSpace(orderByClause))
            {
                return [];
            }

            return SplitTopLevel(orderByClause, ',')
                .Select(item =>
                {
                    var parser = new ExpressionParser(item);
                    var expression = parser.ParseScalarExpression();
                    var descending = parser.MatchKeyword("DESC");
                    if (!descending)
                    {
                        _ = parser.MatchKeyword("ASC");
                    }

                    parser.ExpectEnd("ORDER BY");
                    return new OrderByClause(expression, descending);
                })
                .ToArray();
        }

        private static (string FromAlias, IReadOnlyList<JoinClause> Joins) ParseFromClause(string fromClause)
        {
            var remainder = fromClause.Trim();

            // Detect subquery source: (SELECT ...) AS alias
            if (remainder.StartsWith('('))
            {
                var depth = 0;
                var closeIndex = -1;
                var inString = false;
                for (var i = 0; i < remainder.Length; i++)
                {
                    var ch = remainder[i];
                    if (inString)
                    {
                        if (ch == '\'' && (i + 1 >= remainder.Length || remainder[i + 1] != '\''))
                            inString = false;
                        else if (ch == '\'' && i + 1 < remainder.Length && remainder[i + 1] == '\'')
                            i++;
                        continue;
                    }

                    if (ch == '\'') { inString = true; continue; }
                    if (ch == '(') { depth++; continue; }
                    if (ch == ')')
                    {
                        depth--;
                        if (depth == 0) { closeIndex = i; break; }
                    }
                }

                if (closeIndex < 0)
                    throw CosmosEmulatorException.BadRequest("Unsupported FROM clause: unmatched parenthesis.");

                var afterParen = remainder[(closeIndex + 1)..].Trim();
                string alias;
                if (afterParen.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
                    && afterParen.Length > 2
                    && char.IsWhiteSpace(afterParen[2]))
                {
                    afterParen = afterParen[2..].TrimStart();
                    alias = ReadLeadingIdentifier(afterParen, out _);
                }
                else
                {
                    alias = ReadLeadingIdentifier(afterParen, out _);
                }

                if (string.IsNullOrWhiteSpace(alias))
                    throw CosmosEmulatorException.BadRequest("Subquery in FROM requires an alias.");

                return (alias, []);
            }

            var fromAlias = ReadLeadingIdentifier(remainder, out var consumedLength);
            if (string.IsNullOrWhiteSpace(fromAlias))
            {
                throw CosmosEmulatorException.BadRequest("Unsupported FROM clause.");
            }

            remainder = remainder[consumedLength..].TrimStart();
            var joins = new List<JoinClause>();
            while (!string.IsNullOrWhiteSpace(remainder))
            {
                if (!remainder.StartsWith("JOIN", StringComparison.OrdinalIgnoreCase))
                {
                    throw CosmosEmulatorException.BadRequest("Unsupported FROM clause.");
                }

                remainder = remainder["JOIN".Length..].TrimStart();
                var joinAlias = ReadLeadingIdentifier(remainder, out consumedLength);
                if (string.IsNullOrWhiteSpace(joinAlias))
                {
                    throw CosmosEmulatorException.BadRequest("JOIN requires an alias.");
                }

                remainder = remainder[consumedLength..].TrimStart();
                if (!remainder.StartsWith("IN", StringComparison.OrdinalIgnoreCase)
                    || (remainder.Length > 2 && !char.IsWhiteSpace(remainder[2])))
                {
                    throw CosmosEmulatorException.BadRequest("JOIN must use the 'IN' syntax.");
                }

                remainder = remainder["IN".Length..].TrimStart();
                var nextJoinIndex = FindTopLevelKeyword(remainder, "JOIN", 0);
                var sourceExpression = nextJoinIndex >= 0
                    ? remainder[..nextJoinIndex].Trim()
                    : remainder.Trim();
                if (string.IsNullOrWhiteSpace(sourceExpression))
                {
                    throw CosmosEmulatorException.BadRequest("JOIN source expression is required.");
                }

                joins.Add(new JoinClause(joinAlias, ParseScalarExpression(sourceExpression)));
                remainder = nextJoinIndex >= 0
                    ? remainder[nextJoinIndex..].TrimStart()
                    : string.Empty;
            }

            return (fromAlias, joins);
        }

        private static ScalarExpression ParseScalarExpression(string expression)
        {
            var parser = new ExpressionParser(expression);
            var scalar = parser.ParseScalarExpression();
            parser.ExpectEnd("expression");
            return scalar;
        }

        private static bool ContainsAggregateFunction(ScalarExpression expression)
        {
            return expression switch
            {
                FunctionCallExpression function => AggregateFunctionNames.Contains(function.Name, StringComparer.OrdinalIgnoreCase)
                    || function.Arguments.Any(ContainsAggregateFunction),
                _ => false
            };
        }

        private static string ReadLeadingIdentifier(string text, out int consumedLength)
        {
            consumedLength = 0;
            if (string.IsNullOrWhiteSpace(text) || (!char.IsLetter(text[0]) && text[0] != '_'))
            {
                return string.Empty;
            }

            var index = 1;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
            {
                index++;
            }

            consumedLength = index;
            return text[..index];
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

                if (current is '(' or '[')
                {
                    depth++;
                    continue;
                }

                if (current is ')' or ']')
                {
                    depth = Math.Max(0, depth - 1);
                    continue;
                }

                if (depth == 0 && IsKeywordAt(text, index, keyword))
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
                    case '(' or '[':
                        depth++;
                        break;
                    case ')' or ']':
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
    }

    private sealed class ExpressionParser
    {
        private readonly List<Token> _tokens;
        private int _index;

        public ExpressionParser(string text)
        {
            _tokens = Tokenize(text);
        }

        public BooleanExpression ParseBooleanExpression()
        {
            return ParseOr();
        }

        public ScalarExpression ParseScalarExpression()
        {
            return ParseScalarPrimary();
        }

        public bool MatchKeyword(string keyword)
        {
            if (Current.Type == TokenType.Identifier && string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase))
            {
                _index++;
                return true;
            }

            return false;
        }

        public void ExpectEnd(string context)
        {
            if (Current.Type != TokenType.End)
            {
                throw CosmosEmulatorException.BadRequest($"Unsupported {context} clause.");
            }
        }

        private BooleanExpression ParseOr()
        {
            var expression = ParseAnd();
            while (MatchKeyword("OR"))
            {
                expression = new BinaryBooleanExpression(expression, BooleanOperator.Or, ParseAnd());
            }

            return expression;
        }

        private BooleanExpression ParseAnd()
        {
            var expression = ParseNot();
            while (MatchKeyword("AND"))
            {
                expression = new BinaryBooleanExpression(expression, BooleanOperator.And, ParseNot());
            }

            return expression;
        }

        private BooleanExpression ParseNot()
        {
            if (MatchKeyword("NOT"))
            {
                return new NotBooleanExpression(ParseNot());
            }

            return ParsePredicate();
        }

        private BooleanExpression ParsePredicate()
        {
            if (Current.Type == TokenType.OpenParen)
            {
                _index++;
                var nested = ParseBooleanExpression();
                Expect(TokenType.CloseParen, ")");
                return nested;
            }

            var left = ParseScalarPrimary();
            if (MatchKeyword("NOT"))
            {
                if (!MatchKeyword("IN"))
                {
                    throw CosmosEmulatorException.BadRequest("Unsupported WHERE clause expression.");
                }

                return new InBooleanExpression(left, ParseInList(), true);
            }

            if (MatchKeyword("IN"))
            {
                return new InBooleanExpression(left, ParseInList(), false);
            }

            if (Current.Type == TokenType.Operator)
            {
                var comparison = ParseComparisonOperator();
                var right = ParseScalarPrimary();
                return new ComparisonBooleanExpression(left, comparison, right);
            }

            return new ScalarBooleanExpression(left);
        }

        private IReadOnlyList<ScalarExpression> ParseInList()
        {
            Expect(TokenType.OpenParen, "(");
            var values = new List<ScalarExpression>();
            if (Current.Type != TokenType.CloseParen)
            {
                do
                {
                    values.Add(ParseScalarPrimary());
                }
                while (TryConsume(TokenType.Comma));
            }

            Expect(TokenType.CloseParen, ")");
            return values;
        }

        private ScalarExpression ParseScalarPrimary()
        {
            return Current.Type switch
            {
                TokenType.OpenParen => ParseParenthesizedScalar(),
                TokenType.OpenBracket => ParseArrayLiteral(),
                TokenType.OpenBrace => ParseObjectLiteral(),
                TokenType.Parameter => ConsumeParameter(),
                TokenType.String => ConsumeString(),
                TokenType.Number => ConsumeNumber(),
                TokenType.Asterisk => ConsumeStar(),
                TokenType.Identifier => ConsumeIdentifierLike(),
                _ => throw CosmosEmulatorException.BadRequest("Unsupported expression.")
            };
        }

        private ScalarExpression ParseArrayLiteral()
        {
            _index++; // consume [
            var elements = new List<ScalarExpression>();
            if (Current.Type != TokenType.CloseBracket)
            {
                do
                {
                    elements.Add(ParseScalarPrimary());
                }
                while (TryConsume(TokenType.Comma));
            }

            if (Current.Type != TokenType.CloseBracket)
            {
                throw CosmosEmulatorException.BadRequest("Expected ']'.");
            }

            _index++; // consume ]
            return new ArrayLiteralExpression(elements);
        }

        private ScalarExpression ParseObjectLiteral()
        {
            _index++; // consume {
            var properties = new List<KeyValuePair<string, ScalarExpression>>();
            if (Current.Type != TokenType.CloseBrace)
            {
                do
                {
                    if (Current.Type != TokenType.String && Current.Type != TokenType.Identifier)
                    {
                        throw CosmosEmulatorException.BadRequest(
                            "Expected property name in object literal.");
                    }

                    var key = Current.Text;
                    _index++;
                    Expect(TokenType.Colon, ":");
                    var value = ParseScalarPrimary();
                    properties.Add(new KeyValuePair<string, ScalarExpression>(key, value));
                }
                while (TryConsume(TokenType.Comma));
            }

            if (Current.Type != TokenType.CloseBrace)
            {
                throw CosmosEmulatorException.BadRequest("Expected '}'.");
            }

            _index++; // consume }
            return new ObjectLiteralExpression(properties);
        }

        private ScalarExpression ParseParenthesizedScalar()
        {
            Expect(TokenType.OpenParen, "(");

            if (Current.Type == TokenType.Identifier
                && string.Equals(Current.Text, "SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var innerQuery = CollectInnerQueryText();
                Expect(TokenType.CloseParen, ")");
                return new ScalarSubqueryExpression(innerQuery);
            }

            var expression = ParseScalarPrimary();
            Expect(TokenType.CloseParen, ")");
            return expression;
        }

        private ScalarExpression ConsumeParameter()
        {
            var token = Current;
            _index++;
            return new ParameterExpression(token.Text);
        }

        private ScalarExpression ConsumeString()
        {
            var token = Current;
            _index++;
            return new LiteralExpression(token.Text);
        }

        private ScalarExpression ConsumeNumber()
        {
            var token = Current;
            _index++;
            return new LiteralExpression(double.Parse(token.Text, CultureInfo.InvariantCulture));
        }

        private ScalarExpression ConsumeStar()
        {
            _index++;
            return new StarExpression();
        }

        private ScalarExpression ConsumeIdentifierLike()
        {
            var token = Current;
            _index++;

            if (string.Equals(token.Text, "true", StringComparison.OrdinalIgnoreCase))
            {
                return new LiteralExpression(true);
            }

            if (string.Equals(token.Text, "false", StringComparison.OrdinalIgnoreCase))
            {
                return new LiteralExpression(false);
            }

            if (string.Equals(token.Text, "null", StringComparison.OrdinalIgnoreCase))
            {
                return new LiteralExpression(null);
            }

            if (TryConsume(TokenType.OpenParen))
            {
                var arguments = new List<ScalarExpression>();
                if (Current.Type != TokenType.CloseParen)
                {
                    do
                    {
                        arguments.Add(ParseScalarPrimary());
                    }
                    while (TryConsume(TokenType.Comma));
                }

                Expect(TokenType.CloseParen, ")");
                return new FunctionCallExpression(token.Text, arguments);
            }

            return new PathExpression(token.Text);
        }

        private ComparisonOperator ParseComparisonOperator()
        {
            var token = Current;
            _index++;
            return token.Text switch
            {
                "=" => ComparisonOperator.Equal,
                "!=" => ComparisonOperator.NotEqual,
                ">" => ComparisonOperator.GreaterThan,
                "<" => ComparisonOperator.LessThan,
                ">=" => ComparisonOperator.GreaterThanOrEqual,
                "<=" => ComparisonOperator.LessThanOrEqual,
                _ => throw CosmosEmulatorException.BadRequest("Unsupported comparison operator.")
            };
        }

        private string CollectInnerQueryText()
        {
            var depth = 0;
            var parts = new List<string>();
            while (Current.Type != TokenType.End)
            {
                if (Current.Type == TokenType.OpenParen)
                {
                    depth++;
                }
                else if (Current.Type == TokenType.CloseParen)
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }

                var text = Current.Type == TokenType.String
                    ? "'" + Current.Text.Replace("'", "''") + "'"
                    : Current.Text;
                parts.Add(text);
                _index++;
            }

            return string.Join(" ", parts);
        }

        private bool TryConsume(TokenType tokenType)
        {
            if (Current.Type == tokenType)
            {
                _index++;
                return true;
            }

            return false;
        }

        private void Expect(TokenType tokenType, string tokenText)
        {
            if (!TryConsume(tokenType))
            {
                throw CosmosEmulatorException.BadRequest($"Expected '{tokenText}'.");
            }
        }

        private Token Current => _tokens[_index];

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            for (var index = 0; index < text.Length;)
            {
                var current = text[index];
                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }

                if (current == '@')
                {
                    var start = index++;
                    while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenType.Parameter, text[start..index]));
                    continue;
                }

                if (current == '\'')
                {
                    index++;
                    var builder = new System.Text.StringBuilder();
                    while (index < text.Length)
                    {
                        if (text[index] == '\'')
                        {
                            if (index + 1 < text.Length && text[index + 1] == '\'')
                            {
                                builder.Append('\'');
                                index += 2;
                                continue;
                            }

                            index++;
                            break;
                        }

                        builder.Append(text[index]);
                        index++;
                    }

                    tokens.Add(new Token(TokenType.String, builder.ToString()));
                    continue;
                }

                if ((current == '-' && index + 1 < text.Length && char.IsDigit(text[index + 1]))
                    || char.IsDigit(current))
                {
                    var start = index++;
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.'))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenType.Number, text[start..index]));
                    continue;
                }

                if (char.IsLetter(current) || current == '_')
                {
                    var start = index++;
                    while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '.'))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenType.Identifier, text[start..index]));
                    continue;
                }

                switch (current)
                {
                    case '(':
                        tokens.Add(new Token(TokenType.OpenParen, "("));
                        index++;
                        break;
                    case ')':
                        tokens.Add(new Token(TokenType.CloseParen, ")"));
                        index++;
                        break;
                    case ',':
                        tokens.Add(new Token(TokenType.Comma, ","));
                        index++;
                        break;
                    case '[':
                        tokens.Add(new Token(TokenType.OpenBracket, "["));
                        index++;
                        break;
                    case ']':
                        tokens.Add(new Token(TokenType.CloseBracket, "]"));
                        index++;
                        break;
                    case '{':
                        tokens.Add(new Token(TokenType.OpenBrace, "{"));
                        index++;
                        break;
                    case '}':
                        tokens.Add(new Token(TokenType.CloseBrace, "}"));
                        index++;
                        break;
                    case ':':
                        tokens.Add(new Token(TokenType.Colon, ":"));
                        index++;
                        break;
                    case '*':
                        tokens.Add(new Token(TokenType.Asterisk, "*"));
                        index++;
                        break;
                    case '!' when index + 1 < text.Length && text[index + 1] == '=':
                        tokens.Add(new Token(TokenType.Operator, "!="));
                        index += 2;
                        break;
                    case '>' when index + 1 < text.Length && text[index + 1] == '=':
                        tokens.Add(new Token(TokenType.Operator, ">="));
                        index += 2;
                        break;
                    case '<' when index + 1 < text.Length && text[index + 1] == '=':
                        tokens.Add(new Token(TokenType.Operator, "<="));
                        index += 2;
                        break;
                    case '>' or '<' or '=':
                        tokens.Add(new Token(TokenType.Operator, current.ToString()));
                        index++;
                        break;
                    default:
                        throw CosmosEmulatorException.BadRequest($"Unsupported character '{current}' in expression.");
                }
            }

            tokens.Add(new Token(TokenType.End, string.Empty));
            return tokens;
        }
    }

    private sealed record Token(TokenType Type, string Text);

    private enum TokenType
    {
        End,
        Identifier,
        Parameter,
        String,
        Number,
        OpenParen,
        CloseParen,
        OpenBracket,
        CloseBracket,
        OpenBrace,
        CloseBrace,
        Colon,
        Comma,
        Operator,
        Asterisk
    }
}

public sealed class QueryExplainResult
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    [JsonPropertyName("queryPlan")]
    public JsonObject QueryPlan { get; init; } = [];

    [JsonPropertyName("estimatedRuCharge")]
    public QueryExplainRuCharge EstimatedRuCharge { get; init; } = new();

    [JsonPropertyName("indexAnalysis")]
    public QueryExplainIndexAnalysis IndexAnalysis { get; init; } = new();

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("educationalNotes")]
    public IReadOnlyList<string> EducationalNotes { get; init; } = [];
}

public sealed class QueryExplainRuCharge
{
    [JsonPropertyName("base")]
    public double Base { get; init; }

    [JsonPropertyName("filterCost")]
    public double FilterCost { get; init; }

    [JsonPropertyName("joinCost")]
    public double JoinCost { get; init; }

    [JsonPropertyName("aggregateCost")]
    public double AggregateCost { get; init; }

    [JsonPropertyName("orderByCost")]
    public double OrderByCost { get; init; }

    [JsonPropertyName("crossPartitionMultiplier")]
    public double CrossPartitionMultiplier { get; init; }

    [JsonPropertyName("total")]
    public double Total { get; init; }
}

public sealed class QueryExplainIndexAnalysis
{
    [JsonPropertyName("usedIndexes")]
    public IReadOnlyList<string> UsedIndexes { get; init; } = [];

    [JsonPropertyName("recommendations")]
    public IReadOnlyList<string> Recommendations { get; init; } = [];

    [JsonPropertyName("indexingPolicyPaths")]
    public QueryExplainIndexingPolicyPaths IndexingPolicyPaths { get; init; } = new();
}

public sealed class QueryExplainIndexingPolicyPaths
{
    [JsonPropertyName("included")]
    public IReadOnlyList<string> Included { get; init; } = [];

    [JsonPropertyName("excluded")]
    public IReadOnlyList<string> Excluded { get; init; } = [];
}
