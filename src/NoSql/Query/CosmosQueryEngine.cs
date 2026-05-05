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
    private static readonly AsyncLocal<DateTimeOffset> s_queryNow = new();

    private readonly IDocumentStore _documentStore;
    private readonly IndexValidationService _indexValidation;
    private readonly Lazy<IProgrammabilityEngine?> _programmabilityEngine;

    public CosmosQueryEngine(IDocumentStore documentStore, IndexValidationService indexValidation, IServiceProvider? serviceProvider = null)
    {
        _documentStore = documentStore;
        _indexValidation = indexValidation;
        _programmabilityEngine = new Lazy<IProgrammabilityEngine?>(() =>
            serviceProvider?.GetService(typeof(IProgrammabilityEngine)) as IProgrammabilityEngine);
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

        // Capture a fixed timestamp for the entire query execution so that
        // GetCurrentDateTime / GetCurrentTimestamp / GetCurrentTicks return
        // the same value for every row (matching Cosmos DB behavior).
        s_queryNow.Value = DateTimeOffset.UtcNow;

        var plan = ParseQuery(query, parameters);
        var container = await _documentStore.GetContainerAsync(databaseId, containerId, ct);

        var filterPaths = ExtractFilterPaths(plan.Where);
        var orderByPaths = ExtractOrderByPaths(plan.OrderBy);
        var scanEnabled = options?.EnableScan ?? false;

        var validationResult = _indexValidation.ValidateQuery(
            container.IndexingPolicy, filterPaths, orderByPaths, scanEnabled);

        if (!validationResult.IsAllowed)
        {
            throw CosmosEmulatorException.BadRequest(validationResult.ErrorMessage!);
        }

        var allDocuments = await _documentStore.ListDocumentsAsync(databaseId, containerId, ct);
        var documents = allDocuments.Resources.Where(d => d.IsIndexed).ToList();

        if (options?.PartitionKey is not null)
        {
            documents = documents
                .Where(document => document.PartitionKey.Equals(options.PartitionKey))
                .ToList();
        }

        var matchingRows = new List<QueryRow>();
        if (plan.FromSubquery is not null)
        {
            // FROM subquery: execute the inner query and use results as the row source
            var subqueryResult = await ExecuteQueryAsync(databaseId, containerId, plan.FromSubquery.InnerQuery, parameters, ct: ct);
            foreach (var subRow in subqueryResult.Resources)
            {
                ct.ThrowIfCancellationRequested();

                var seedRow = new QueryRow(
                    null,
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [plan.FromAlias] = NormalizeRuntimeValue(subRow)
                    });

                if (plan.Where is null || EvaluateBooleanExpression(seedRow, plan.Where, parameters, new SubqueryContext(databaseId, containerId)))
                {
                    matchingRows.Add(seedRow);
                }
            }
        }
        else
        {
            foreach (var document in documents)
            {
                ct.ThrowIfCancellationRequested();

                var responseBody = document.ToResponseBody();
                var seedRow = new QueryRow(
                    document,
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [plan.FromAlias] = responseBody
                    });

                foreach (var joinedRow in ApplyJoins(seedRow, plan.Joins, parameters, databaseId, containerId))
                {
                    if (plan.Where is null || EvaluateBooleanExpression(joinedRow, plan.Where, parameters, new SubqueryContext(databaseId, containerId)))
                    {
                        matchingRows.Add(joinedRow);
                    }
                }
            }
        }

        List<JsonObject> projectedResults;
        if (plan.RequiresAggregation)
        {
            projectedResults = ExecuteAggregateQuery(matchingRows, plan, parameters, databaseId, containerId);
        }
        else
        {
            var orderedRows = ApplyOrdering(matchingRows, plan, parameters, databaseId, containerId);
            var windowedRows = ApplyWindowing(orderedRows, plan.Top, plan.Offset, plan.Limit);
            projectedResults = windowedRows
                .Select(row => ProjectRow(row, plan, parameters, databaseId, containerId))
                .ToList();
        }

        if (plan.Distinct)
        {
            projectedResults = projectedResults
                .DistinctBy(result => result.ToJsonString())
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
                : null,
            RuMultiplier = validationResult.RuMultiplier,
            IsValueProjection = plan.Projection.Mode == ProjectionMode.Value
        };
    }

    private static List<string> ExtractFilterPaths(BooleanExpression? where)
    {
        var paths = new List<string>();
        if (where is not null)
        {
            CollectFilterPaths(where, paths);
        }

        return paths;
    }

    private static void CollectFilterPaths(BooleanExpression expression, List<string> paths)
    {
        switch (expression)
        {
            case BinaryBooleanExpression binary:
                CollectFilterPaths(binary.Left, paths);
                CollectFilterPaths(binary.Right, paths);
                break;
            case NotBooleanExpression not:
                CollectFilterPaths(not.Expression, paths);
                break;
            case ComparisonBooleanExpression comparison:
                AddPathFromScalar(comparison.Left, paths);
                AddPathFromScalar(comparison.Right, paths);
                break;
            case InBooleanExpression inExpr:
                AddPathFromScalar(inExpr.Left, paths);
                break;
            case LikeBooleanExpression like:
                AddPathFromScalar(like.Left, paths);
                break;
            case ScalarBooleanExpression scalar:
                AddPathFromScalar(scalar.Expression, paths);
                break;
        }
    }

    private static void AddPathFromScalar(ScalarExpression expression, List<string> paths)
    {
        if (expression is PathExpression pathExpr)
        {
            var indexPath = IndexValidationService.ConvertToIndexPath(pathExpr.Path);
            if (indexPath is not null)
            {
                paths.Add(indexPath);
            }
        }
        else if (expression is FunctionCallExpression func)
        {
            foreach (var arg in func.Arguments)
            {
                AddPathFromScalar(arg, paths);
            }
        }
    }

    private static List<(string path, bool descending)> ExtractOrderByPaths(IReadOnlyList<OrderByClause> orderByClauses)
    {
        var paths = new List<(string path, bool descending)>();
        foreach (var clause in orderByClauses)
        {
            if (clause.Expression is PathExpression pathExpr)
            {
                var indexPath = IndexValidationService.ConvertToIndexPath(pathExpr.Path);
                if (indexPath is not null)
                {
                    paths.Add((indexPath, clause.Descending));
                }
            }
        }

        return paths;
    }

    private QueryPlan ParseQuery(string query, IReadOnlyDictionary<string, object?>? parameters)
    {
        var trimmedQuery = StripSqlComments(query.Trim()).TrimEnd(';').Trim();
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
        var groupByIndex = FindTopLevelKeyword(trimmedQuery, "GROUP BY", fromIndex + "FROM".Length);
        var orderByIndex = FindTopLevelKeyword(trimmedQuery, "ORDER BY", fromIndex + "FROM".Length);
        var offsetIndex = FindTopLevelKeyword(trimmedQuery, "OFFSET", fromIndex + "FROM".Length);

        var clauseIndexes = new[] { whereIndex, groupByIndex, orderByIndex, offsetIndex }
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToArray();

        var fromClauseEnd = clauseIndexes.FirstOrDefault(trimmedQuery.Length);
        var fromClause = trimmedQuery[(fromIndex + "FROM".Length)..fromClauseEnd].Trim();
        var (fromAlias, joins, fromSubquery, arrayIterationSource) = ParseFromClause(fromClause);

        string? whereClause = null;
        if (whereIndex >= 0)
        {
            var whereEnd = new[] { groupByIndex, orderByIndex, offsetIndex }
                .Where(index => index > whereIndex)
                .OrderBy(index => index)
                .FirstOrDefault(trimmedQuery.Length);
            whereClause = trimmedQuery[(whereIndex + "WHERE".Length)..whereEnd].Trim();
        }

        string? groupByClause = null;
        if (groupByIndex >= 0)
        {
            var groupByEnd = new[] { orderByIndex, offsetIndex }
                .Where(index => index > groupByIndex)
                .OrderBy(index => index)
                .FirstOrDefault(trimmedQuery.Length);
            groupByClause = trimmedQuery[(groupByIndex + "GROUP BY".Length)..groupByEnd].Trim();
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
        var distinct = ParseDistinct(ref selectClause);
        var projection = ParseProjection(selectClause);
        var where = ParseWhere(whereClause);
        var groupBy = ParseGroupBy(groupByClause);
        var orderBy = ParseOrderBy(orderByClause);
        var (offset, limit) = ParseOffsetLimit(offsetLimitClause, parameters);

        if (projection.Mode == ProjectionMode.All && (groupBy.Count > 0 || projection.ContainsAggregate))
        {
            throw CosmosEmulatorException.BadRequest("SELECT * is not supported for aggregate queries.");
        }

        return new QueryPlan(fromAlias, joins, projection, where, groupBy, orderBy, top, offset, limit, distinct, fromSubquery, arrayIterationSource);
    }

    /// <summary>
    /// Strips single-line (<c>-- ...</c>) and multi-line (<c>/* ... */</c>) SQL comments from query text.
    /// </summary>
    internal static string StripSqlComments(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                // Single-line comment: skip to end of line
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                if (i < sql.Length)
                    i++; // skip the newline itself
            }
            else if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                // Multi-line comment: skip to closing */
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;
                if (i + 1 < sql.Length)
                    i += 2; // skip */
            }
            else if (sql[i] == '\'' || sql[i] == '"')
            {
                // String literal: copy verbatim (don't strip comments inside strings)
                var quote = sql[i];
                sb.Append(sql[i]);
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == quote)
                    {
                        i++;
                        // Handle escaped quotes (double quote)
                        if (i < sql.Length && sql[i] == quote)
                        {
                            sb.Append(sql[i]);
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            else
            {
                sb.Append(sql[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool ParseDistinct(ref string selectClause)
    {
        if (selectClause.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase)
            && selectClause.Length > "DISTINCT".Length
            && char.IsWhiteSpace(selectClause["DISTINCT".Length]))
        {
            selectClause = selectClause["DISTINCT".Length..].Trim();
            return true;
        }
        return false;
    }

    private int? ParseTop(ref string selectClause, IReadOnlyDictionary<string, object?>? parameters)
    {
        var topMatch = Regex.Match(selectClause, @"^TOP\s+(?<value>@?[A-Za-z0-9_\-\.]+)\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
        if (!topMatch.Success)
        {
            return null;
        }

        selectClause = topMatch.Groups["rest"].Value.Trim();
        return ResolveNonNegativeInteger(ParseScalarExpression(topMatch.Groups["value"].Value), parameters, "TOP");
    }

    private static Projection ParseProjection(string selectClause)
    {
        if (string.Equals(selectClause, "*", StringComparison.Ordinal))
        {
            return new Projection(ProjectionMode.All, []);
        }

        if (selectClause.StartsWith("VALUE", StringComparison.OrdinalIgnoreCase))
        {
            var valueExpression = ParseScalarExpression(selectClause["VALUE".Length..].Trim());
            return new Projection(ProjectionMode.Value, [new SelectItem(valueExpression, "$1")]);
        }

        var fieldTexts = SplitTopLevel(selectClause, ',');
        if (fieldTexts.Count == 0)
        {
            throw CosmosEmulatorException.BadRequest("SELECT must project at least one field.");
        }

        var fields = new List<SelectItem>();
        for (var i = 0; i < fieldTexts.Count; i++)
        {
            var parser = new ExpressionParser(fieldTexts[i]);
            var expression = parser.ParseScalarExpression();
            var alias = parser.TryConsumeAlias();
            parser.ExpectEnd("expression");
            fields.Add(new SelectItem(expression, alias ?? GetOutputAlias(expression, i + 1)));
        }

        return new Projection(ProjectionMode.Fields, fields);
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
            .ToList();
    }

    private static IReadOnlyList<OrderByClause> ParseOrderBy(string? orderByClause)
    {
        if (string.IsNullOrWhiteSpace(orderByClause))
        {
            return [];
        }

        var clauses = new List<OrderByClause>();
        var parser = new ExpressionParser(orderByClause);

        do
        {
            var expression = parser.ParseScalarExpression();
            var descending = parser.MatchKeyword("DESC");
            if (!descending)
            {
                _ = parser.MatchKeyword("ASC");
            }

            clauses.Add(new OrderByClause(expression, descending));
        }
        while (parser.TryConsumeComma());

        parser.ExpectEnd("ORDER BY");
        return clauses;
    }

    private static (string FromAlias, IReadOnlyList<JoinClause> Joins, SubqueryFromSource? FromSubquery, ScalarExpression? ArrayIterationSource) ParseFromClause(string fromClause)
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

            var innerQuery = remainder[1..closeIndex].Trim();
            var afterParen = remainder[(closeIndex + 1)..].Trim();

            // Expect optional AS keyword followed by alias
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

            return (alias, [], new SubqueryFromSource(innerQuery, alias), null);
        }

        var fromAlias = ReadLeadingIdentifier(remainder, out var consumedLength);
        if (string.IsNullOrWhiteSpace(fromAlias))
        {
            throw CosmosEmulatorException.BadRequest("Unsupported FROM clause.");
        }

        remainder = remainder[consumedLength..].TrimStart();

        // Detect FROM alias IN expr (array iteration, used in correlated subqueries)
        if (remainder.StartsWith("IN", StringComparison.OrdinalIgnoreCase)
            && (remainder.Length <= 2 || char.IsWhiteSpace(remainder[2])))
        {
            var sourceText = remainder["IN".Length..].TrimStart();
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                throw CosmosEmulatorException.BadRequest("FROM ... IN requires a source expression.");
            }

            return (fromAlias, [], null, ParseScalarExpression(sourceText));
        }

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

        return (fromAlias, joins, null, null);
    }

    private (int? offset, int? limit) ParseOffsetLimit(string? offsetLimitClause, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(offsetLimitClause))
        {
            return (null, null);
        }

        var limitIndex = FindTopLevelKeyword(offsetLimitClause, "LIMIT", 0);
        if (limitIndex < 0)
        {
            throw CosmosEmulatorException.BadRequest("OFFSET queries must include LIMIT.");
        }

        var offsetText = offsetLimitClause[..limitIndex].Trim();
        var limitText = offsetLimitClause[(limitIndex + "LIMIT".Length)..].Trim();
        var offset = ResolveNonNegativeInteger(ParseScalarExpression(offsetText), parameters, "OFFSET");
        var limit = ResolveNonNegativeInteger(ParseScalarExpression(limitText), parameters, "LIMIT");
        return (offset, limit);
    }

    private static ScalarExpression ParseScalarExpression(string expression)
    {
        var parser = new ExpressionParser(expression);
        var scalar = parser.ParseScalarExpression();
        parser.ExpectEnd("expression");
        return scalar;
    }

    private List<QueryRow> ApplyJoins(QueryRow seedRow, IReadOnlyList<JoinClause> joins, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        var rows = new List<QueryRow> { seedRow };
        foreach (var join in joins)
        {
            var expandedRows = new List<QueryRow>();
            foreach (var row in rows)
            {
                var source = EvaluateScalarExpression(row, join.SourceExpression, parameters, databaseId: databaseId, containerId: containerId);
                if (source is not JsonArray array)
                {
                    continue;
                }

                foreach (var item in array)
                {
                    expandedRows.Add(row.WithAlias(join.Alias, NormalizeRuntimeValue(item)));
                }
            }

            rows = expandedRows;
            if (rows.Count == 0)
            {
                break;
            }
        }

        return rows;
    }

    private List<QueryRow> ApplyOrdering(List<QueryRow> rows, QueryPlan plan, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        if (plan.OrderBy.Count == 0)
        {
            return rows
                .OrderBy(row => row.Document?.Timestamp)
                .ThenBy(row => row.Document?.Id, StringComparer.Ordinal)
                .ToList();
        }

        var first = plan.OrderBy[0];
        Func<QueryRow, object?> firstKey = row => EvaluateScalarExpression(row, first.Expression, parameters, databaseId: databaseId, containerId: containerId);
        IOrderedEnumerable<QueryRow> orderedRows = first.Descending
            ? rows.OrderByDescending(firstKey, QueryValueComparer.Instance)
            : rows.OrderBy(firstKey, QueryValueComparer.Instance);

        for (var i = 1; i < plan.OrderBy.Count; i++)
        {
            var clause = plan.OrderBy[i];
            Func<QueryRow, object?> key = row => EvaluateScalarExpression(row, clause.Expression, parameters, databaseId: databaseId, containerId: containerId);
            orderedRows = clause.Descending
                ? orderedRows.ThenByDescending(key, QueryValueComparer.Instance)
                : orderedRows.ThenBy(key, QueryValueComparer.Instance);
        }

        return orderedRows
            .ThenBy(row => row.Document?.Id, StringComparer.Ordinal)
            .ToList();
    }

    private List<JsonObject> ExecuteAggregateQuery(List<QueryRow> rows, QueryPlan plan, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        var groups = BuildGroups(rows, plan, parameters, databaseId, containerId);
        var projected = groups
            .Select(group => ProjectAggregateGroup(group, plan, parameters, databaseId, containerId))
            .ToList();

        projected = ApplyProjectedOrdering(projected, plan, parameters, databaseId, containerId);
        return ApplyWindowing(projected, plan.Top, plan.Offset, plan.Limit);
    }

    private List<QueryGroup> BuildGroups(List<QueryRow> rows, QueryPlan plan, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        if (plan.GroupBy.Count == 0)
        {
            return [new QueryGroup([], rows)];
        }

        var groups = new List<QueryGroup>();
        foreach (var row in rows)
        {
            var keyValues = plan.GroupBy
                .Select(expression => EvaluateScalarExpression(row, expression, parameters, databaseId: databaseId, containerId: containerId))
                .ToList();

            var existingGroup = groups.FirstOrDefault(group => KeysMatch(group.KeyValues, keyValues));
            if (existingGroup is null)
            {
                existingGroup = new QueryGroup(keyValues, []);
                groups.Add(existingGroup);
            }

            existingGroup.Rows.Add(row);
        }

        return groups;
    }

    private static bool KeysMatch(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!AreEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private List<JsonObject> ApplyProjectedOrdering(List<JsonObject> rows, QueryPlan plan, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        if (plan.OrderBy.Count == 0 || rows.Count == 0)
        {
            return rows;
        }

        QueryRow ToQueryRow(JsonObject row) => new(
            null,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [plan.FromAlias] = row
            });

        var first = plan.OrderBy[0];
        Func<JsonObject, object?> firstKey = row => EvaluateScalarExpression(ToQueryRow(row), first.Expression, parameters, group: null, databaseId: databaseId, containerId: containerId);
        IOrderedEnumerable<JsonObject> ordered = first.Descending
            ? rows.OrderByDescending(firstKey, QueryValueComparer.Instance)
            : rows.OrderBy(firstKey, QueryValueComparer.Instance);

        for (var i = 1; i < plan.OrderBy.Count; i++)
        {
            var clause = plan.OrderBy[i];
            Func<JsonObject, object?> key = row => EvaluateScalarExpression(ToQueryRow(row), clause.Expression, parameters, group: null, databaseId: databaseId, containerId: containerId);
            ordered = clause.Descending
                ? ordered.ThenByDescending(key, QueryValueComparer.Instance)
                : ordered.ThenBy(key, QueryValueComparer.Instance);
        }

        return ordered.ToList();
    }

    private static List<T> ApplyWindowing<T>(List<T> items, int? top, int? offset, int? limit)
    {
        IEnumerable<T> window = items;
        if (top is int topValue)
        {
            window = window.Take(topValue);
        }

        if (offset is int offsetValue)
        {
            window = window.Skip(offsetValue);
        }

        if (limit is int limitValue)
        {
            window = window.Take(limitValue);
        }

        return window.ToList();
    }

    private JsonObject ProjectRow(QueryRow row, QueryPlan plan, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        return plan.Projection.Mode switch
        {
            ProjectionMode.All => ProjectAll(row, plan.FromAlias),
            ProjectionMode.Fields => ProjectFields(row, plan.Projection.Items, parameters, databaseId: databaseId, containerId: containerId),
            ProjectionMode.Value => ProjectValue(row, plan.Projection.Items[0].Expression, parameters, databaseId: databaseId, containerId: containerId),
            _ => throw CosmosEmulatorException.BadRequest("Unsupported projection.")
        };
    }

    private JsonObject ProjectAggregateGroup(QueryGroup group, QueryPlan plan, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        var representativeRow = group.Rows.FirstOrDefault();
        return plan.Projection.Mode switch
        {
            ProjectionMode.Fields => ProjectFields(representativeRow, plan.Projection.Items, parameters, group.Rows, databaseId, containerId),
            ProjectionMode.Value => ProjectValue(representativeRow, plan.Projection.Items[0].Expression, parameters, group.Rows, databaseId, containerId),
            _ => throw CosmosEmulatorException.BadRequest("Unsupported aggregate projection.")
        };
    }

    private static JsonObject ProjectAll(QueryRow row, string fromAlias)
    {
        if (!row.Aliases.TryGetValue(fromAlias, out var source) || source is not JsonObject document)
        {
            return new JsonObject();
        }

        return document.DeepClone().AsObject();
    }

    private JsonObject ProjectFields(QueryRow? row, IReadOnlyList<SelectItem> items, IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<QueryRow>? group = null, string? databaseId = null, string? containerId = null)
    {
        var projected = new JsonObject();
        foreach (var item in items)
        {
            var value = EvaluateScalarExpression(row, item.Expression, parameters, group, databaseId, containerId);
            if (ReferenceEquals(value, UndefinedValue))
            {
                continue;
            }

            projected[item.OutputName] = ConvertToJsonNode(value);
        }

        return projected;
    }

    private JsonObject ProjectValue(QueryRow? row, ScalarExpression expression, IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<QueryRow>? group = null, string? databaseId = null, string? containerId = null)
    {
        var projected = new JsonObject();
        var value = EvaluateScalarExpression(row, expression, parameters, group, databaseId, containerId);
        projected["$1"] = ReferenceEquals(value, UndefinedValue) ? null : ConvertToJsonNode(value);
        return projected;
    }

    private bool EvaluateBooleanExpression(QueryRow row, BooleanExpression expression, IReadOnlyDictionary<string, object?>? parameters, SubqueryContext? subqueryContext = null)
    {
        return expression switch
        {
            BinaryBooleanExpression binary => binary.Operator switch
            {
                BooleanOperator.And => EvaluateBooleanExpression(row, binary.Left, parameters, subqueryContext)
                    && EvaluateBooleanExpression(row, binary.Right, parameters, subqueryContext),
                BooleanOperator.Or => EvaluateBooleanExpression(row, binary.Left, parameters, subqueryContext)
                    || EvaluateBooleanExpression(row, binary.Right, parameters, subqueryContext),
                _ => throw CosmosEmulatorException.BadRequest("Unsupported boolean operator.")
            },
            NotBooleanExpression unary => !EvaluateBooleanExpression(row, unary.Expression, parameters, subqueryContext),
            ComparisonBooleanExpression comparison => EvaluateComparison(row, comparison, parameters, subqueryContext?.DatabaseId, subqueryContext?.ContainerId),
            InBooleanExpression inExpression => EvaluateIn(row, inExpression, parameters, subqueryContext?.DatabaseId, subqueryContext?.ContainerId),
            LikeBooleanExpression likeExpr => EvaluateLike(row, likeExpr, parameters, subqueryContext?.DatabaseId, subqueryContext?.ContainerId),
            SubqueryInBooleanExpression subqueryIn => EvaluateSubqueryIn(row, subqueryIn, parameters, subqueryContext!),
            ExistsBooleanExpression exists => EvaluateExists(exists, parameters, subqueryContext!),
            ScalarBooleanExpression scalar => ToBoolean(EvaluateScalarExpression(row, scalar.Expression, parameters, databaseId: subqueryContext?.DatabaseId, containerId: subqueryContext?.ContainerId)),
            _ => throw CosmosEmulatorException.BadRequest("Unsupported WHERE clause expression.")
        };
    }

    private bool EvaluateComparison(QueryRow row, ComparisonBooleanExpression expression, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        var left = EvaluateScalarExpression(row, expression.Left, parameters, databaseId: databaseId, containerId: containerId);
        var right = EvaluateScalarExpression(row, expression.Right, parameters, databaseId: databaseId, containerId: containerId);
        if (ReferenceEquals(left, UndefinedValue) || ReferenceEquals(right, UndefinedValue))
        {
            return false;
        }

        return expression.Operator switch
        {
            ComparisonOperator.Equal => AreEqual(left, right),
            ComparisonOperator.NotEqual => !AreEqual(left, right),
            ComparisonOperator.GreaterThan => CompareValues(left, right) > 0,
            ComparisonOperator.LessThan => CompareValues(left, right) < 0,
            ComparisonOperator.GreaterThanOrEqual => CompareValues(left, right) >= 0,
            ComparisonOperator.LessThanOrEqual => CompareValues(left, right) <= 0,
            _ => throw CosmosEmulatorException.BadRequest("Unsupported comparison operator.")
        };
    }

    private bool EvaluateIn(QueryRow row, InBooleanExpression expression, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        var left = EvaluateScalarExpression(row, expression.Left, parameters, databaseId: databaseId, containerId: containerId);
        if (ReferenceEquals(left, UndefinedValue))
        {
            return false;
        }

        var found = expression.Values.Any(value =>
        {
            var candidate = EvaluateScalarExpression(row, value, parameters, databaseId: databaseId, containerId: containerId);
            return !ReferenceEquals(candidate, UndefinedValue) && AreEqual(left, candidate);
        });

        return expression.Negated ? !found : found;
    }

    private bool EvaluateSubqueryIn(QueryRow row, SubqueryInBooleanExpression expression, IReadOnlyDictionary<string, object?>? parameters, SubqueryContext context)
    {
        var left = EvaluateScalarExpression(row, expression.Left, parameters);
        if (ReferenceEquals(left, UndefinedValue))
        {
            return false;
        }

        var result = ExecuteQueryAsync(context.DatabaseId, context.ContainerId, expression.InnerQuery, parameters)
            .GetAwaiter().GetResult();

        var found = result.Resources.Any(obj =>
        {
            // SELECT VALUE queries produce {"$1": value}; extract the scalar value
            var val = obj.Count == 1 && obj.ContainsKey("$1")
                ? NormalizeRuntimeValue(obj["$1"])
                : (object)obj;
            return !ReferenceEquals(val, UndefinedValue) && AreEqual(left, val);
        });

        return expression.Negated ? !found : found;
    }

    private bool EvaluateExists(ExistsBooleanExpression expression, IReadOnlyDictionary<string, object?>? parameters, SubqueryContext context)
    {
        var result = ExecuteQueryAsync(context.DatabaseId, context.ContainerId, expression.InnerQuery, parameters)
            .GetAwaiter().GetResult();
        return result.Resources.Count > 0;
    }

    private object? EvaluateArrayLiteral(
        QueryRow? row,
        ArrayLiteralExpression arrayLit,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<QueryRow>? group = null,
        string? databaseId = null,
        string? containerId = null)
    {
        var array = new JsonArray();
        foreach (var element in arrayLit.Elements)
        {
            var value = EvaluateScalarExpression(row, element, parameters, group, databaseId, containerId);
            if (!ReferenceEquals(value, UndefinedValue))
            {
                array.Add(ConvertToJsonNode(value));
            }
        }

        return array;
    }

    private object? EvaluateScalarSubquery(ScalarSubqueryExpression subquery, QueryRow? outerRow, IReadOnlyDictionary<string, object?>? parameters, string? databaseId, string? containerId)
    {
        if (databaseId is null || containerId is null)
        {
            throw CosmosEmulatorException.BadRequest("Scalar subqueries require a database and container context.");
        }

        // Parse the inner query to check for correlated FROM ... IN ... pattern
        var innerPlan = ParseQuery(subquery.InnerQuery, parameters);

        if (innerPlan.ArrayIterationSource is not null && outerRow is not null)
        {
            return EvaluateCorrelatedSubquery(innerPlan, outerRow, parameters, databaseId, containerId);
        }

        // Independent subquery — execute recursively
        var result = ExecuteQueryAsync(databaseId, containerId, subquery.InnerQuery, parameters)
            .GetAwaiter().GetResult();

        if (result.Resources.Count == 0)
        {
            return UndefinedValue;
        }

        if (result.Resources.Count > 1)
        {
            throw CosmosEmulatorException.BadRequest("Scalar subquery must return at most one row.");
        }

        var row = result.Resources[0];

        // SELECT VALUE queries produce {"$1": value}; unwrap the scalar
        if (row.Count == 1 && row.ContainsKey("$1"))
        {
            return NormalizeRuntimeValue(row["$1"]);
        }

        return NormalizeRuntimeValue(row);
    }

    private object? EvaluateCorrelatedSubquery(QueryPlan plan, QueryRow outerRow, IReadOnlyDictionary<string, object?>? parameters, string databaseId, string containerId)
    {
        // Resolve the array source against the outer row
        var sourceValue = EvaluateScalarExpression(outerRow, plan.ArrayIterationSource!, parameters, databaseId: databaseId, containerId: containerId);
        if (sourceValue is not JsonArray array)
        {
            return UndefinedValue;
        }

        // Build rows from array elements, carrying forward outer aliases
        var rows = new List<QueryRow>();
        foreach (var item in array)
        {
            var aliases = new Dictionary<string, object?>(outerRow.Aliases, StringComparer.OrdinalIgnoreCase)
            {
                [plan.FromAlias] = NormalizeRuntimeValue(item)
            };
            rows.Add(new QueryRow(null, aliases));
        }

        // Apply JOINs if any
        if (plan.Joins.Count > 0)
        {
            var joinedRows = new List<QueryRow>();
            foreach (var row in rows)
            {
                joinedRows.AddRange(ApplyJoins(row, plan.Joins, parameters, databaseId, containerId));
            }
            rows = joinedRows;
        }

        // Apply WHERE filter
        if (plan.Where is not null)
        {
            rows = rows.Where(r => EvaluateBooleanExpression(r, plan.Where, parameters, new SubqueryContext(databaseId, containerId))).ToList();
        }

        // Handle aggregation
        if (plan.RequiresAggregation)
        {
            var results = ExecuteAggregateQuery(rows, plan, parameters, databaseId, containerId);
            if (results.Count == 0)
            {
                return UndefinedValue;
            }

            var aggRow = results[0];
            if (aggRow.Count == 1 && aggRow.ContainsKey("$1"))
            {
                return NormalizeRuntimeValue(aggRow["$1"]);
            }

            return NormalizeRuntimeValue(aggRow);
        }

        // Non-aggregate projection
        if (rows.Count == 0)
        {
            return UndefinedValue;
        }

        var projectedResults = rows
            .Select(row => ProjectRow(row, plan, parameters, databaseId, containerId))
            .ToList();

        if (projectedResults.Count > 1)
        {
            throw CosmosEmulatorException.BadRequest("Scalar subquery must return at most one row.");
        }

        var result = projectedResults[0];
        if (result.Count == 1 && result.ContainsKey("$1"))
        {
            return NormalizeRuntimeValue(result["$1"]);
        }

        return NormalizeRuntimeValue(result);
    }

    private bool EvaluateLike(QueryRow row, LikeBooleanExpression expression, IReadOnlyDictionary<string, object?>? parameters, string? databaseId = null, string? containerId = null)
    {
        var left = EvaluateScalarExpression(row, expression.Left, parameters, databaseId: databaseId, containerId: containerId);
        var pattern = EvaluateScalarExpression(row, expression.Pattern, parameters, databaseId: databaseId, containerId: containerId);
        if (left is not string str || pattern is not string pat)
            return false;

        // Convert SQL LIKE pattern to regex: % = .*, _ = ., escape others
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pat)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(str, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool ToBoolean(object? value)
    {
        return value is bool boolean && boolean;
    }

    private object? EvaluateScalarExpression(
        QueryRow? row,
        ScalarExpression expression,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<QueryRow>? group = null,
        string? databaseId = null,
        string? containerId = null)
    {
        return expression switch
        {
            LiteralExpression literal => literal.Value,
            ParameterExpression parameter => ResolveParameter(parameter.Name, parameters),
            PathExpression path => row is null ? UndefinedValue : ResolvePathValue(row, path.Path),
            FunctionCallExpression function => EvaluateFunction(row, function, parameters, group, databaseId, containerId),
            ScalarSubqueryExpression subquery => EvaluateScalarSubquery(subquery, row, parameters, databaseId, containerId),
            ArrayLiteralExpression arrayLit => EvaluateArrayLiteral(row, arrayLit, parameters, group, databaseId, containerId),
            StarExpression => UndefinedValue,
            _ => throw CosmosEmulatorException.BadRequest("Unsupported expression.")
        };
    }

    private object? EvaluateFunction(
        QueryRow? row,
        FunctionCallExpression function,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<QueryRow>? group,
        string? databaseId = null,
        string? containerId = null)
    {
        if (IsAggregateFunction(function.Name))
        {
            if (group is null)
            {
                throw CosmosEmulatorException.BadRequest($"Aggregate function '{function.Name}' is not supported in this context.");
            }

            return EvaluateAggregateFunction(function, group, parameters, databaseId, containerId);
        }

        var arguments = function.Arguments
            .Select(argument => EvaluateScalarExpression(row, argument, parameters, group, databaseId, containerId))
            .ToList();

        if (function.Name.StartsWith("udf.", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateUdf(function.Name[4..], arguments, databaseId, containerId);
        }

        if (string.Equals(function.Name, "VECTORDISTANCE", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateVectorDistance(arguments, databaseId, containerId);
        }

        return EvaluateBuiltInFunction(function.Name, arguments);
    }

    private object? EvaluateUdf(string udfName, IReadOnlyList<object?> arguments, string? databaseId, string? containerId)
    {
        if (_programmabilityEngine.Value is null || databaseId is null || containerId is null)
        {
            throw CosmosEmulatorException.BadRequest($"Cannot execute UDF '{udfName}': programmability engine or context not available.");
        }

        var udf = _programmabilityEngine.Value.GetUdfAsync(databaseId, containerId, udfName)
            .GetAwaiter().GetResult();

        var engine = new Jint.Engine();
        engine.Execute(udf.Body);

        var jsArguments = arguments.Select(a => Jint.Native.JsValue.FromObject(engine, a)).ToArray();
        var result = engine.Invoke(udf.Id, jsArguments);

        return result.Type switch
        {
            Jint.Runtime.Types.Undefined => UndefinedValue,
            Jint.Runtime.Types.Null => null,
            _ => NormalizeRuntimeValue(result.ToObject())
        };
    }

    private object? EvaluateVectorDistance(IReadOnlyList<object?> arguments, string? databaseId, string? containerId)
    {
        if (arguments.Count < 2)
        {
            throw CosmosEmulatorException.BadRequest("VectorDistance requires at least two arguments.");
        }

        var vec1 = ExtractVector(arguments[0]);
        var vec2 = ExtractVector(arguments[1]);
        if (vec1 is null || vec2 is null)
        {
            return UndefinedValue;
        }

        if (vec1.Length != vec2.Length)
        {
            throw CosmosEmulatorException.BadRequest("VectorDistance vectors must have the same number of dimensions.");
        }

        // Determine distance function: default is cosine
        var distanceFunction = "cosine";

        // 4th argument: options object with distanceFunction override
        if (arguments.Count >= 4 && arguments[3] is JsonObject options)
        {
            if (options["distanceFunction"]?.GetValue<string>() is string df)
            {
                distanceFunction = df;
            }
        }

        // If no override, try looking up the container's vector embedding policy
        if (arguments.Count < 4 && databaseId is not null && containerId is not null)
        {
            var container = _documentStore.GetContainerAsync(databaseId, containerId)
                .GetAwaiter().GetResult();
            if (container.VectorEmbeddingPolicy?.VectorEmbeddings is { Count: > 0 } embeddings)
            {
                distanceFunction = embeddings[0].DistanceFunction;
            }
        }

        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => ComputeCosineSimilarity(vec1, vec2),
            "dotproduct" or "dot product" => ComputeDotProduct(vec1, vec2),
            "euclidean" => ComputeEuclideanDistance(vec1, vec2),
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported distance function '{distanceFunction}'.")
        };
    }

    private static double[]? ExtractVector(object? value)
    {
        if (value is JsonArray array)
        {
            var vector = new double[array.Count];
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is null)
                {
                    return null;
                }

                if (TryConvertToDouble(NormalizeRuntimeValue(array[i]), out var d))
                {
                    vector[i] = d;
                }
                else
                {
                    return null;
                }
            }

            return vector;
        }

        return null;
    }

    private static double ComputeCosineSimilarity(double[] a, double[] b)
    {
        var dot = 0.0;
        var magA = 0.0;
        var magB = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = Math.Sqrt(magA) * Math.Sqrt(magB);
        return magnitude == 0 ? 0 : dot / magnitude;
    }

    private static double ComputeDotProduct(double[] a, double[] b)
    {
        var dot = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    private static double ComputeEuclideanDistance(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }

        return Math.Sqrt(sum);
    }

    private static bool IsAggregateFunction(string functionName)
    {
        return functionName.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
    }

    private object? EvaluateAggregateFunction(
        FunctionCallExpression function,
        IReadOnlyList<QueryRow> group,
        IReadOnlyDictionary<string, object?>? parameters,
        string? databaseId = null,
        string? containerId = null)
    {
        var name = function.Name.ToUpperInvariant();
        if (function.Arguments.Count != 1)
        {
            throw CosmosEmulatorException.BadRequest($"Function '{function.Name}' expects a single argument.");
        }

        var argument = function.Arguments[0];
        if (name == "COUNT")
        {
            if (argument is StarExpression)
            {
                return group.Count;
            }

            if (argument is LiteralExpression literal
                && literal.Value is double numericLiteral
                && numericLiteral.Equals(1d))
            {
                return group.Count;
            }

            return group.Count(row =>
            {
                var value = EvaluateScalarExpression(row, argument, parameters, databaseId: databaseId, containerId: containerId);
                return !ReferenceEquals(value, UndefinedValue) && value is not null;
            });
        }

        var values = group
            .Select(row => EvaluateScalarExpression(row, argument, parameters, databaseId: databaseId, containerId: containerId))
            .Where(value => !ReferenceEquals(value, UndefinedValue) && value is not null)
            .ToList();

        return name switch
        {
            "SUM" => SumValues(values),
            "AVG" => AverageValues(values),
            "MIN" => MinOrMax(values, descending: false),
            "MAX" => MinOrMax(values, descending: true),
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported aggregate function '{function.Name}'.")
        };
    }

    private static object? SumValues(IReadOnlyList<object?> values)
    {
        var numericValues = values
            .Select(value => TryConvertToDouble(value, out var number) ? number : (double?)null)
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .ToList();

        return numericValues.Count == 0 ? null : numericValues.Sum();
    }

    private static object? AverageValues(IReadOnlyList<object?> values)
    {
        var numericValues = values
            .Select(value => TryConvertToDouble(value, out var number) ? number : (double?)null)
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .ToList();

        return numericValues.Count == 0 ? null : numericValues.Average();
    }

    private static object? MinOrMax(IReadOnlyList<object?> values, bool descending)
    {
        if (values.Count == 0)
        {
            return null;
        }

        return descending
            ? values.MaxBy(value => value, QueryValueComparer.Instance)
            : values.MinBy(value => value, QueryValueComparer.Instance);
    }

    private static object? EvaluateBuiltInFunction(string functionName, IReadOnlyList<object?> arguments)
    {
        var name = functionName.ToUpperInvariant();
        return name switch
        {
            "CONTAINS" => EvaluateContains(arguments),
            "ARRAY_CONTAINS" => EvaluateArrayContains(arguments),
            "IS_DEFINED" => EvaluateIsDefined(arguments),
            "STARTSWITH" => EvaluateStartsOrEndsWith(arguments, startsWith: true),
            "ENDSWITH" => EvaluateStartsOrEndsWith(arguments, startsWith: false),
            "UPPER" => EvaluateUnaryString(arguments, value => value.ToUpperInvariant()),
            "LOWER" => EvaluateUnaryString(arguments, value => value.ToLowerInvariant()),
            "SUBSTRING" => EvaluateSubstring(arguments),
            "CONCAT" => EvaluateConcat(arguments),
            "LENGTH" => EvaluateLength(arguments),
            "REPLACE" => EvaluateReplace(arguments),
            "TRIM" => EvaluateUnaryString(arguments, value => value.Trim()),
            "LEFT" => EvaluateLeftOrRight(arguments, left: true),
            "RIGHT" => EvaluateLeftOrRight(arguments, left: false),
            "IS_STRING" => EvaluateTypeCheck(arguments, value => value is string),
            "IS_NUMBER" => EvaluateTypeCheck(arguments, value => TryConvertToDouble(value, out _)),
            "IS_BOOL" => EvaluateTypeCheck(arguments, value => value is bool),
            "IS_NULL" => EvaluateTypeCheck(arguments, value => value is null),
            "IS_ARRAY" => EvaluateTypeCheck(arguments, value => value is JsonArray),
            "IS_OBJECT" => EvaluateTypeCheck(arguments, value => value is JsonObject),
            "IS_PRIMITIVE" => EvaluateTypeCheck(arguments, value => value is null or string or bool || TryConvertToDouble(value, out _)),
            "IS_INTEGER" => EvaluateTypeCheck(arguments, value => TryConvertToDouble(value, out var d) && Math.Abs(d % 1) < double.Epsilon),
            "IS_FINITE" => EvaluateTypeCheck(arguments, value => TryConvertToDouble(value, out var d) && double.IsFinite(d)),
            "IS_NAN" => EvaluateTypeCheck(arguments, value => value is double d && double.IsNaN(d)),
            "ABS" => EvaluateUnaryNumber(arguments, Math.Abs),
            "CEILING" => EvaluateUnaryNumber(arguments, Math.Ceiling),
            "FLOOR" => EvaluateUnaryNumber(arguments, Math.Floor),
            "ROUND" => EvaluateUnaryNumber(arguments, Math.Round),
            "POWER" => EvaluatePower(arguments),
            "SQRT" => EvaluateSqrt(arguments),
            // Advanced math
            "LOG" => EvaluateUnaryNumber(arguments, Math.Log),
            "LOG10" => EvaluateUnaryNumber(arguments, Math.Log10),
            "EXP" => EvaluateUnaryNumber(arguments, Math.Exp),
            "SIN" => EvaluateUnaryNumber(arguments, Math.Sin),
            "COS" => EvaluateUnaryNumber(arguments, Math.Cos),
            "TAN" => EvaluateUnaryNumber(arguments, Math.Tan),
            "ACOS" => EvaluateUnaryNumber(arguments, Math.Acos),
            "ASIN" => EvaluateUnaryNumber(arguments, Math.Asin),
            "ATAN" => EvaluateUnaryNumber(arguments, Math.Atan),
            "ATN2" => EvaluateBinaryNumber(arguments, Math.Atan2),
            "COT" => EvaluateUnaryNumber(arguments, v => 1.0 / Math.Tan(v)),
            "SQUARE" => EvaluateUnaryNumber(arguments, v => v * v),
            "RAND" => Random.Shared.NextDouble(),
            "NUMBERBIN" => EvaluateNumberBin(arguments),
            "SIGN" => EvaluateUnaryNumber(arguments, v => Math.Sign(v)),
            "TRUNC" => EvaluateUnaryNumber(arguments, Math.Truncate),
            "PI" => Math.PI,
            "DEGREES" => EvaluateUnaryNumber(arguments, v => v * (180.0 / Math.PI)),
            "RADIANS" => EvaluateUnaryNumber(arguments, v => v * (Math.PI / 180.0)),
            // Integer math
            "INTADD" => EvaluateIntBinaryOp(arguments, (a, b) => checked(a + b)),
            "INTSUB" => EvaluateIntBinaryOp(arguments, (a, b) => checked(a - b)),
            "INTMUL" => EvaluateIntBinaryOp(arguments, (a, b) => checked(a * b)),
            "INTDIV" => EvaluateIntBinaryOp(arguments, (a, b) => b == 0 ? throw new OverflowException() : checked(a / b)),
            "INTMOD" => EvaluateIntBinaryOp(arguments, (a, b) => b == 0 ? throw new OverflowException() : a % b),
            "INTBITAND" => EvaluateIntBinaryOp(arguments, (a, b) => a & b),
            "INTBITOR" => EvaluateIntBinaryOp(arguments, (a, b) => a | b),
            "INTBITXOR" => EvaluateIntBinaryOp(arguments, (a, b) => a ^ b),
            "INTBITNOT" => EvaluateIntUnaryOp(arguments, a => ~a),
            "INTBITLEFTSHIFT" => EvaluateIntBinaryOp(arguments, (a, b) => a << (int)b),
            "INTBITRIGHTSHIFT" => EvaluateIntBinaryOp(arguments, (a, b) => a >> (int)b),
            // Advanced string
            "REVERSE" => EvaluateUnaryString(arguments, v => new string(v.Reverse().ToArray())),
            "LTRIM" => EvaluateUnaryString(arguments, v => v.TrimStart()),
            "RTRIM" => EvaluateUnaryString(arguments, v => v.TrimEnd()),
            "TOSTRING" => arguments.Count >= 1 ? arguments[0]?.ToString() : throw CosmosEmulatorException.BadRequest("ToString expects one argument."),
            "REPLICATE" => EvaluateReplicate(arguments),
            "REGEXMATCH" => EvaluateRegexMatch(arguments),
            "INDEX_OF" => EvaluateIndexOf(arguments),
            "STRINGEQUALS" => EvaluateStringEquals(arguments),
            "STRINGTOARRAY" => EvaluateStringToArray(arguments),
            "STRINGTOBOOLEAN" => EvaluateStringToBoolean(arguments),
            "STRINGTONULL" => EvaluateStringToNull(arguments),
            "STRINGTONUMBER" => EvaluateStringToNumber(arguments),
            "STRINGTOOBJECT" => EvaluateStringToObject(arguments),
            // Conditional
            "IIF" => EvaluateIif(arguments),
            // Advanced array
            "ARRAY_LENGTH" => EvaluateArrayLength(arguments),
            "ARRAY_CONCAT" => EvaluateArrayConcat(arguments),
            "ARRAY_SLICE" => EvaluateArraySlice(arguments),
            "ARRAY_CONTAINS_ALL" => EvaluateArrayContainsAllOrAny(arguments, all: true),
            "ARRAY_CONTAINS_ANY" => EvaluateArrayContainsAllOrAny(arguments, all: false),
            "SETINTERSECT" => EvaluateSetOperation(arguments, intersect: true),
            "SETUNION" => EvaluateSetOperation(arguments, intersect: false),
            // Date/time (use per-query captured timestamp for consistency)
            "GETCURRENTDATETIME" => s_queryNow.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            "GETCURRENTTIMESTAMP" => s_queryNow.Value.ToUnixTimeMilliseconds(),
            "GETCURRENTTICKS" => s_queryNow.Value.Ticks,
            "DATETIMEADD" => EvaluateDateTimeAdd(arguments),
            "DATETIMEDIFF" => EvaluateDateTimeDiff(arguments),
            "DATETIMEPART" => EvaluateDateTimePart(arguments),
            "DATETIMETOTICKS" => arguments.Count >= 1 && arguments[0] is string dtStr && DateTimeOffset.TryParse(dtStr, out var dto) ? dto.Ticks : throw CosmosEmulatorException.BadRequest("DateTimeToTicks expects a valid datetime string."),
            "TICKSTODATETIME" => arguments.Count >= 1 && TryConvertToDouble(arguments[0], out var ticks) ? new DateTimeOffset((long)ticks, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") : throw CosmosEmulatorException.BadRequest("TicksToDateTime expects a numeric ticks value."),
            "DATETIMEBIN" => EvaluateDateTimeBin(arguments),
            "DATETIMEFROMPARTS" => EvaluateDateTimeFromParts(arguments),
            "DATETIMETOTIMESTAMP" => EvaluateDateTimeToTimestamp(arguments),
            "TIMESTAMPTODATETIME" => EvaluateTimestampToDateTime(arguments),
            // Full-text search (simplified emulator approximation)
            "FULLTEXTCONTAINS" => EvaluateFullTextContains(arguments),
            "FULLTEXTCONTAINSALL" => EvaluateFullTextContainsAllOrAny(arguments, all: true),
            "FULLTEXTCONTAINSANY" => EvaluateFullTextContainsAllOrAny(arguments, all: false),
            "FULLTEXTSCORE" => EvaluateFullTextScore(arguments),
            // Spatial functions
            "ST_DISTANCE" => EvaluateStDistance(arguments),
            "ST_WITHIN" => EvaluateStWithin(arguments),
            "ST_INTERSECTS" => EvaluateStIntersects(arguments),
            "ST_ISVALID" => EvaluateStIsValid(arguments),
            "ST_ISVALIDDETAILED" => EvaluateStIsValidDetailed(arguments),
            "ST_AREA" => EvaluateStArea(arguments),
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported function '{functionName}'.")
        };
    }

    private static object? EvaluateContains(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2)
        {
            throw CosmosEmulatorException.BadRequest("CONTAINS expects two arguments.");
        }

        return arguments[0] is string input && arguments[1] is string search
            && input.Contains(search, StringComparison.Ordinal);
    }

    private static object? EvaluateArrayContains(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count is < 2 or > 3)
        {
            throw CosmosEmulatorException.BadRequest("ARRAY_CONTAINS expects two or three arguments.");
        }

        if (arguments[0] is not JsonArray array)
        {
            return false;
        }

        var partialMatch = arguments.Count == 3 && arguments[2] is true;

        foreach (var item in array)
        {
            var normalized = NormalizeRuntimeValue(item);
            if (partialMatch && normalized is JsonObject itemObj && arguments[1] is JsonObject searchObj)
            {
                if (IsPartialMatch(itemObj, searchObj))
                    return true;
            }
            else if (AreEqual(normalized, arguments[1]))
            {
                return true;
            }
        }

        return false;
    }

    private static object? EvaluateIsDefined(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
        {
            throw CosmosEmulatorException.BadRequest("IS_DEFINED expects one argument.");
        }

        return !ReferenceEquals(arguments[0], UndefinedValue);
    }

    private static object? EvaluateStartsOrEndsWith(IReadOnlyList<object?> arguments, bool startsWith)
    {
        if (arguments.Count != 2)
        {
            throw CosmosEmulatorException.BadRequest(startsWith ? "STARTSWITH expects two arguments." : "ENDSWITH expects two arguments.");
        }

        if (arguments[0] is not string input || arguments[1] is not string prefixOrSuffix)
        {
            return false;
        }

        return startsWith
            ? input.StartsWith(prefixOrSuffix, StringComparison.Ordinal)
            : input.EndsWith(prefixOrSuffix, StringComparison.Ordinal);
    }

    private static object? EvaluateUnaryString(IReadOnlyList<object?> arguments, Func<string, string> transform)
    {
        if (arguments.Count != 1)
        {
            throw CosmosEmulatorException.BadRequest("Function expects one argument.");
        }

        return arguments[0] is string value ? transform(value) : null;
    }

    private static object? EvaluateSubstring(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 3)
        {
            throw CosmosEmulatorException.BadRequest("SUBSTRING expects three arguments.");
        }

        if (arguments[0] is not string value
            || !TryConvertToInt32(arguments[1], out var start)
            || !TryConvertToInt32(arguments[2], out var length)
            || start < 0
            || length < 0)
        {
            return null;
        }

        if (start >= value.Length)
        {
            return string.Empty;
        }

        var safeLength = Math.Min(length, value.Length - start);
        return value.Substring(start, safeLength);
    }

    private static object? EvaluateConcat(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2)
        {
            throw CosmosEmulatorException.BadRequest("CONCAT expects at least two arguments.");
        }

        if (arguments.Any(argument => argument is null || ReferenceEquals(argument, UndefinedValue)))
        {
            return null;
        }

        return string.Concat(arguments.Select(argument => Convert.ToString(argument, CultureInfo.InvariantCulture)));
    }

    private static object? EvaluateLength(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
        {
            throw CosmosEmulatorException.BadRequest("LENGTH expects one argument.");
        }

        return arguments[0] is string value ? value.Length : null;
    }

    private static object? EvaluateReplace(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 3)
        {
            throw CosmosEmulatorException.BadRequest("REPLACE expects three arguments.");
        }

        return arguments[0] is string value && arguments[1] is string oldValue && arguments[2] is string newValue
            ? value.Replace(oldValue, newValue, StringComparison.Ordinal)
            : null;
    }

    private static object? EvaluateLeftOrRight(IReadOnlyList<object?> arguments, bool left)
    {
        if (arguments.Count != 2)
        {
            throw CosmosEmulatorException.BadRequest(left ? "LEFT expects two arguments." : "RIGHT expects two arguments.");
        }

        if (arguments[0] is not string value || !TryConvertToInt32(arguments[1], out var count) || count < 0)
        {
            return null;
        }

        var safeCount = Math.Min(count, value.Length);
        return left
            ? value[..safeCount]
            : value[^safeCount..];
    }

    private static object? EvaluateTypeCheck(IReadOnlyList<object?> arguments, Func<object?, bool> predicate)
    {
        if (arguments.Count != 1)
        {
            throw CosmosEmulatorException.BadRequest("Type-checking functions expect one argument.");
        }

        return !ReferenceEquals(arguments[0], UndefinedValue) && predicate(arguments[0]);
    }

    private static object? EvaluateUnaryNumber(IReadOnlyList<object?> arguments, Func<double, double> transform)
    {
        if (arguments.Count != 1)
        {
            throw CosmosEmulatorException.BadRequest("Math function expects one argument.");
        }

        if (!TryConvertToDouble(arguments[0], out var value))
        {
            return null;
        }

        var result = transform(value);
        return double.IsNaN(result) || double.IsInfinity(result) ? null : result;
    }

    private static object? EvaluatePower(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2)
        {
            throw CosmosEmulatorException.BadRequest("POWER expects two arguments.");
        }

        if (!TryConvertToDouble(arguments[0], out var value) || !TryConvertToDouble(arguments[1], out var exponent))
        {
            return null;
        }

        var result = Math.Pow(value, exponent);
        return double.IsNaN(result) || double.IsInfinity(result) ? null : result;
    }

    private static object? EvaluateSqrt(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
        {
            throw CosmosEmulatorException.BadRequest("SQRT expects one argument.");
        }

        if (!TryConvertToDouble(arguments[0], out var value))
        {
            return null;
        }

        var result = Math.Sqrt(value);
        return double.IsNaN(result) || double.IsInfinity(result) ? null : result;
    }

    private static object? EvaluateReplicate(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2) throw CosmosEmulatorException.BadRequest("REPLICATE expects two arguments.");
        if (arguments[0] is not string s) return null;
        if (!TryConvertToDouble(arguments[1], out var count)) return null;
        var n = (int)count;
        return n <= 0 ? "" : string.Concat(Enumerable.Repeat(s, n));
    }

    private static object? EvaluateRegexMatch(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2) throw CosmosEmulatorException.BadRequest("RegexMatch expects at least two arguments.");
        if (arguments[0] is not string input || arguments[1] is not string pattern) return null;
        var options = arguments.Count >= 3 && arguments[2] is string flags && flags.Contains('i')
            ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
            : System.Text.RegularExpressions.RegexOptions.None;
        return System.Text.RegularExpressions.Regex.IsMatch(input, pattern, options);
    }

    private static object? EvaluateArrayLength(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1) throw CosmosEmulatorException.BadRequest("ARRAY_LENGTH expects one argument.");
        return arguments[0] is JsonArray arr ? arr.Count : (object?)null;
    }

    private static object? EvaluateArrayConcat(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2) throw CosmosEmulatorException.BadRequest("ARRAY_CONCAT expects at least two arguments.");
        var result = new JsonArray();
        foreach (var arg in arguments)
        {
            if (arg is JsonArray arr)
                foreach (var item in arr)
                    result.Add(item?.DeepClone());
        }
        return result;
    }

    private static object? EvaluateArraySlice(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2) throw CosmosEmulatorException.BadRequest("ARRAY_SLICE expects at least two arguments.");
        if (arguments[0] is not JsonArray arr) return null;
        if (!TryConvertToDouble(arguments[1], out var startD)) return null;
        var start = (int)startD;
        if (start < 0) start = Math.Max(0, arr.Count + start);
        var length = arguments.Count >= 3 && TryConvertToDouble(arguments[2], out var lenD) ? (int)lenD : arr.Count - start;
        var result = new JsonArray();
        for (var i = start; i < Math.Min(start + length, arr.Count); i++)
            result.Add(arr[i]?.DeepClone());
        return result;
    }

    private static object? EvaluateDateTimeAdd(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 3) throw CosmosEmulatorException.BadRequest("DateTimeAdd expects three arguments.");
        if (arguments[0] is not string part || !TryConvertToDouble(arguments[1], out var amount) || arguments[2] is not string dateStr)
            return null;
        if (!DateTimeOffset.TryParse(dateStr, out var dt)) return null;
        var n = (int)amount;
        dt = part.ToLowerInvariant() switch
        {
            "year" or "yy" or "yyyy" => dt.AddYears(n),
            "month" or "mm" or "m" => dt.AddMonths(n),
            "day" or "dd" or "d" => dt.AddDays(n),
            "hour" or "hh" => dt.AddHours(n),
            "minute" or "mi" or "n" => dt.AddMinutes(n),
            "second" or "ss" or "s" => dt.AddSeconds(n),
            "millisecond" or "ms" => dt.AddMilliseconds(n),
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported DateTimeAdd part: '{part}'.")
        };
        return dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }

    private static object? EvaluateDateTimeDiff(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 3) throw CosmosEmulatorException.BadRequest("DateTimeDiff expects three arguments.");
        if (arguments[0] is not string part || arguments[1] is not string startStr || arguments[2] is not string endStr)
            return null;
        if (!DateTimeOffset.TryParse(startStr, out var start) || !DateTimeOffset.TryParse(endStr, out var end))
            return null;
        var diff = end - start;
        return part.ToLowerInvariant() switch
        {
            "year" or "yy" or "yyyy" => end.Year - start.Year,
            "month" or "mm" or "m" => (end.Year - start.Year) * 12 + end.Month - start.Month,
            "day" or "dd" or "d" => (long)diff.TotalDays,
            "hour" or "hh" => (long)diff.TotalHours,
            "minute" or "mi" or "n" => (long)diff.TotalMinutes,
            "second" or "ss" or "s" => (long)diff.TotalSeconds,
            "millisecond" or "ms" => (long)diff.TotalMilliseconds,
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported DateTimeDiff part: '{part}'.")
        };
    }

    private static object? EvaluateDateTimePart(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2) throw CosmosEmulatorException.BadRequest("DateTimePart expects two arguments.");
        if (arguments[0] is not string part || arguments[1] is not string dateStr) return null;
        if (!DateTimeOffset.TryParse(dateStr, out var dt)) return null;
        return part.ToLowerInvariant() switch
        {
            "year" or "yy" or "yyyy" => dt.Year,
            "month" or "mm" or "m" => dt.Month,
            "day" or "dd" or "d" => dt.Day,
            "hour" or "hh" => dt.Hour,
            "minute" or "mi" or "n" => dt.Minute,
            "second" or "ss" or "s" => dt.Second,
            "millisecond" or "ms" => dt.Millisecond,
            "dayofweek" or "dw" => (int)dt.DayOfWeek,
            "dayofyear" or "dy" => dt.DayOfYear,
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported DateTimePart part: '{part}'.")
        };
    }

    private static object? EvaluateBinaryNumber(IReadOnlyList<object?> arguments, Func<double, double, double> transform)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest("Math function expects two arguments.");
        if (!TryConvertToDouble(arguments[0], out var a) || !TryConvertToDouble(arguments[1], out var b))
            return null;
        var result = transform(a, b);
        return double.IsNaN(result) || double.IsInfinity(result) ? null : result;
    }

    private static object? EvaluateNumberBin(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest("NumberBin expects two arguments.");
        if (!TryConvertToDouble(arguments[0], out var value) || !TryConvertToDouble(arguments[1], out var binSize))
            return null;
        if (binSize == 0) return null;
        return Math.Floor(value / binSize) * binSize;
    }

    private static bool TryConvertToInt64(object? value, out long result)
    {
        switch (value)
        {
            case byte b: result = b; return true;
            case sbyte sb: result = sb; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case int i: result = i; return true;
            case uint ui: result = ui; return true;
            case long l: result = l; return true;
            case float f when Math.Abs(f % 1) < float.Epsilon && f is >= long.MinValue and <= long.MaxValue:
                result = (long)f; return true;
            case double d when Math.Abs(d % 1) < double.Epsilon && d is >= long.MinValue and <= long.MaxValue:
                result = (long)d; return true;
            case decimal m when m == decimal.Truncate(m) && m is >= long.MinValue and <= long.MaxValue:
                result = (long)m; return true;
            default:
                result = default; return false;
        }
    }

    private static object? EvaluateIntBinaryOp(IReadOnlyList<object?> arguments, Func<long, long, long> op)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest("Integer math function expects two arguments.");
        if (!TryConvertToInt64(arguments[0], out var a) || !TryConvertToInt64(arguments[1], out var b))
            return UndefinedValue;
        try { return op(a, b); }
        catch (OverflowException) { return UndefinedValue; }
    }

    private static object? EvaluateIntUnaryOp(IReadOnlyList<object?> arguments, Func<long, long> op)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("Integer math function expects one argument.");
        if (!TryConvertToInt64(arguments[0], out var a))
            return UndefinedValue;
        try { return op(a); }
        catch (OverflowException) { return UndefinedValue; }
    }

    private static object? EvaluateIndexOf(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count is < 2 or > 3)
            throw CosmosEmulatorException.BadRequest("INDEX_OF expects two or three arguments.");
        if (arguments[0] is not string input || arguments[1] is not string search)
            return null;
        var startIndex = 0;
        if (arguments.Count == 3)
        {
            if (!TryConvertToInt32(arguments[2], out startIndex) || startIndex < 0)
                return null;
            if (startIndex >= input.Length)
                return -1;
        }
        return input.IndexOf(search, startIndex, StringComparison.Ordinal);
    }

    private static object? EvaluateStringEquals(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count is < 2 or > 3)
            throw CosmosEmulatorException.BadRequest("StringEquals expects two or three arguments.");
        if (arguments[0] is not string str1 || arguments[1] is not string str2)
            return null;
        var ignoreCase = arguments.Count == 3 && arguments[2] is true;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return str1.Equals(str2, comparison);
    }

    private static object? EvaluateStringToArray(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("StringToArray expects one argument.");
        if (arguments[0] is not string input)
            return UndefinedValue;
        try
        {
            var node = JsonNode.Parse(input.Trim());
            return node is JsonArray array ? array : UndefinedValue;
        }
        catch
        {
            return UndefinedValue;
        }
    }

    private static object? EvaluateSetOperation(IReadOnlyList<object?> arguments, bool intersect)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest(intersect ? "SetIntersect expects two arguments." : "SetUnion expects two arguments.");
        if (arguments[0] is not JsonArray arr1 || arguments[1] is not JsonArray arr2)
            return UndefinedValue;

        var result = new JsonArray();
        if (intersect)
        {
            foreach (var item in arr1)
            {
                var normalized = NormalizeRuntimeValue(item);
                var existsInArr2 = arr2.Any(b => AreEqual(normalized, NormalizeRuntimeValue(b)));
                var alreadyInResult = result.Any(r => AreEqual(normalized, NormalizeRuntimeValue(r)));
                if (existsInArr2 && !alreadyInResult)
                    result.Add(item?.DeepClone());
            }
        }
        else
        {
            foreach (var item in arr1)
            {
                var normalized = NormalizeRuntimeValue(item);
                if (!result.Any(r => AreEqual(normalized, NormalizeRuntimeValue(r))))
                    result.Add(item?.DeepClone());
            }
            foreach (var item in arr2)
            {
                var normalized = NormalizeRuntimeValue(item);
                if (!result.Any(r => AreEqual(normalized, NormalizeRuntimeValue(r))))
                    result.Add(item?.DeepClone());
            }
        }
        return result;
    }

    // ── STRINGTO* functions (Cosmos DB JSON-parsing semantics) ───────────

    private static object? EvaluateStringToBoolean(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("StringToBoolean expects one argument.");
        if (arguments[0] is not string input)
            return UndefinedValue;
        var trimmed = input.Trim();
        if (trimmed == "true") return true;
        if (trimmed == "false") return false;
        return UndefinedValue;
    }

    private static object? EvaluateStringToNull(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("StringToNull expects one argument.");
        if (arguments[0] is not string input)
            return UndefinedValue;
        return input.Trim() == "null" ? null : UndefinedValue;
    }

    private static object? EvaluateStringToNumber(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("StringToNumber expects one argument.");
        if (arguments[0] is not string input)
            return UndefinedValue;
        var trimmed = input.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
            return (double)longVal;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
            return dblVal;
        return UndefinedValue;
    }

    private static object? EvaluateStringToObject(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("StringToObject expects one argument.");
        if (arguments[0] is not string input)
            return UndefinedValue;
        try
        {
            var node = JsonNode.Parse(input.Trim());
            return node is JsonObject obj ? obj : UndefinedValue;
        }
        catch
        {
            return UndefinedValue;
        }
    }

    // ── IIF conditional ─────────────────────────────────────────────────

    private static object? EvaluateIif(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 3)
            throw CosmosEmulatorException.BadRequest("IIF expects three arguments.");
        // Cosmos DB IIF: only boolean true returns the true-branch; all other values (numbers, strings, arrays, objects, null) return the false-branch.
        return arguments[0] is true ? arguments[1] : arguments[2];
    }

    // ── ARRAY_CONTAINS_ALL / ARRAY_CONTAINS_ANY ─────────────────────────

    private static object? EvaluateArrayContainsAllOrAny(IReadOnlyList<object?> arguments, bool all)
    {
        if (arguments.Count < 2)
            throw CosmosEmulatorException.BadRequest($"ARRAY_CONTAINS_{(all ? "ALL" : "ANY")} expects at least two arguments.");
        if (arguments[0] is not JsonArray array)
            return false;

        for (var i = 1; i < arguments.Count; i++)
        {
            var found = array.Any(item => AreEqual(NormalizeRuntimeValue(item), arguments[i]));
            if (all && !found) return false;
            if (!all && found) return true;
        }

        return all;
    }

    // ── Partial object matching helper (for ARRAY_CONTAINS 3rd param) ───

    private static bool IsPartialMatch(JsonObject candidate, JsonObject search)
    {
        foreach (var prop in search)
        {
            if (!candidate.TryGetPropertyValue(prop.Key, out var candidateValue))
                return false;
            if (!AreEqual(NormalizeRuntimeValue(candidateValue), NormalizeRuntimeValue(prop.Value)))
                return false;
        }
        return true;
    }

    // ── Full-text search (simplified emulator approximation) ────────────

    private static object? EvaluateFullTextContains(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest("FullTextContains expects two arguments.");
        if (arguments[0] is not string text || arguments[1] is not string search)
            return false;
        return text.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static object? EvaluateFullTextContainsAllOrAny(IReadOnlyList<object?> arguments, bool all)
    {
        if (arguments.Count < 2)
            throw CosmosEmulatorException.BadRequest($"FullTextContains{(all ? "All" : "Any")} expects at least two arguments.");
        if (arguments[0] is not string text)
            return false;

        for (var i = 1; i < arguments.Count; i++)
        {
            if (arguments[i] is not string term) continue;
            var found = text.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (all && !found) return false;
            if (!all && found) return true;
        }

        return all;
    }

    private static object? EvaluateFullTextScore(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2)
            throw CosmosEmulatorException.BadRequest("FullTextScore expects at least two arguments.");
        if (arguments[0] is not string text)
            return 0.0;

        var score = 0.0;
        for (var i = 1; i < arguments.Count; i++)
        {
            if (arguments[i] is string term && text.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1.0;
        }
        return score;
    }

    private static object? EvaluateDateTimeBin(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count is < 3 or > 4)
            throw CosmosEmulatorException.BadRequest("DateTimeBin expects three or four arguments.");
        if (arguments[0] is not string dateStr || arguments[1] is not string part || !TryConvertToDouble(arguments[2], out var binSizeD))
            return null;
        if (!DateTimeOffset.TryParse(dateStr, out var dt))
            return null;
        var binSize = (long)binSizeD;
        if (binSize <= 0)
            throw CosmosEmulatorException.BadRequest("DateTimeBin bin size must be positive.");

        var origin = arguments.Count == 4 && arguments[3] is string originStr && DateTimeOffset.TryParse(originStr, out var o)
            ? o
            : new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var ticksPerUnit = part.ToLowerInvariant() switch
        {
            "year" or "yy" or "yyyy" => TimeSpan.TicksPerDay * 365,
            "month" or "mm" or "m" => TimeSpan.TicksPerDay * 30,
            "week" or "wk" or "ww" => TimeSpan.TicksPerDay * 7,
            "day" or "dd" or "d" => TimeSpan.TicksPerDay,
            "hour" or "hh" => TimeSpan.TicksPerHour,
            "minute" or "mi" or "n" => TimeSpan.TicksPerMinute,
            "second" or "ss" or "s" => TimeSpan.TicksPerSecond,
            "millisecond" or "ms" => TimeSpan.TicksPerMillisecond,
            _ => throw CosmosEmulatorException.BadRequest($"Unsupported DateTimeBin part: '{part}'.")
        };

        var totalBinTicks = ticksPerUnit * binSize;
        var offsetTicks = dt.Ticks - origin.Ticks;
        var binNumber = (long)Math.Floor((double)offsetTicks / totalBinTicks);
        var resultTicks = origin.Ticks + binNumber * totalBinTicks;
        var result = new DateTimeOffset(resultTicks, TimeSpan.Zero);
        return result.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }

    private static object? EvaluateDateTimeFromParts(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 7)
            throw CosmosEmulatorException.BadRequest("DateTimeFromParts expects seven arguments (year, month, day, hour, minute, second, millisecond).");
        if (!TryConvertToInt32(arguments[0], out var year) ||
            !TryConvertToInt32(arguments[1], out var month) ||
            !TryConvertToInt32(arguments[2], out var day) ||
            !TryConvertToInt32(arguments[3], out var hour) ||
            !TryConvertToInt32(arguments[4], out var minute) ||
            !TryConvertToInt32(arguments[5], out var second) ||
            !TryConvertToInt32(arguments[6], out var millisecond))
            return null;
        try
        {
            var dt = new DateTimeOffset(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);
            return dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static object? EvaluateDateTimeToTimestamp(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("DateTimeToTimestamp expects one argument.");
        if (arguments[0] is not string dateStr || !DateTimeOffset.TryParse(dateStr, out var dt))
            return null;
        return dt.ToUnixTimeMilliseconds();
    }

    private static object? EvaluateTimestampToDateTime(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("TimestampToDateTime expects one argument.");
        if (!TryConvertToDouble(arguments[0], out var ms))
            return null;
        var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        return dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }

    private static object? ResolvePathValue(QueryRow row, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !row.Aliases.TryGetValue(segments[0], out var current))
        {
            return UndefinedValue;
        }

        if (segments.Length == 1)
        {
            return current;
        }

        for (var index = 1; index < segments.Length; index++)
        {
            var segment = segments[index];
            switch (current)
            {
                case JsonObject currentObject when currentObject.TryGetPropertyValue(segment, out var propertyValue):
                    current = NormalizeRuntimeValue(propertyValue);
                    break;
                case JsonArray currentArray when int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arrayIndex)
                                               && arrayIndex >= 0
                                               && arrayIndex < currentArray.Count:
                    current = NormalizeRuntimeValue(currentArray[arrayIndex]);
                    break;
                default:
                    return UndefinedValue;
            }
        }

        return current;
    }

    private static object? ResolveParameter(string parameterName, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null)
        {
            throw CosmosEmulatorException.BadRequest($"Missing query parameter '{parameterName}'.");
        }

        if (!parameters.TryGetValue(parameterName, out var value)
            && !parameters.TryGetValue(parameterName.TrimStart('@'), out value))
        {
            throw CosmosEmulatorException.BadRequest($"Missing query parameter '{parameterName}'.");
        }

        return NormalizeRuntimeValue(value);
    }

    private int ResolveNonNegativeInteger(ScalarExpression expression, IReadOnlyDictionary<string, object?>? parameters, string clauseName)
    {
        var resolved = EvaluateScalarExpression(null, expression, parameters);
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
            uint number => (double)number,
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

    private static JsonNode? ConvertToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string stringValue => JsonValue.Create(stringValue),
            bool boolValue => JsonValue.Create(boolValue),
            byte byteValue => JsonValue.Create(byteValue),
            sbyte sbyteValue => JsonValue.Create(sbyteValue),
            short shortValue => JsonValue.Create(shortValue),
            ushort ushortValue => JsonValue.Create(ushortValue),
            int intValue => JsonValue.Create(intValue),
            uint uintValue => JsonValue.Create(uintValue),
            long longValue => JsonValue.Create(longValue),
            ulong ulongValue => JsonValue.Create(ulongValue),
            float floatValue => JsonValue.Create(floatValue),
            double doubleValue => JsonValue.Create(doubleValue),
            decimal decimalValue => JsonValue.Create(decimalValue),
            _ => JsonSerializer.SerializeToNode(value)
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

    private static string GetOutputAlias(ScalarExpression expression, int ordinal)
    {
        return expression switch
        {
            PathExpression path => GetPathAlias(path.Path),
            _ => $"${ordinal}"
        };
    }

    private static string GetPathAlias(string path)
    {
        var lastDot = path.LastIndexOf('.');
        return lastDot >= 0 ? path[(lastDot + 1)..] : path;
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
            if (MatchKeyword("EXISTS"))
            {
                Expect(TokenType.OpenParen, "(");
                var innerQuery = CollectInnerQueryText();
                Expect(TokenType.CloseParen, ")");
                return new ExistsBooleanExpression(innerQuery);
            }

            if (Current.Type == TokenType.OpenParen)
            {
                // Peek ahead: if the next token is SELECT, treat as scalar subquery
                if (_index + 1 < _tokens.Count
                    && _tokens[_index + 1].Type == TokenType.Identifier
                    && string.Equals(_tokens[_index + 1].Text, "SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse as scalar subquery, then continue to comparison
                    var subqueryScalar = ParseScalarPrimary();
                    if (Current.Type == TokenType.Operator)
                    {
                        var comparison = ParseComparisonOperator();
                        var right = ParseScalarPrimary();
                        return new ComparisonBooleanExpression(subqueryScalar, comparison, right);
                    }

                    return new ScalarBooleanExpression(subqueryScalar);
                }

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

                return ParseInOrSubqueryIn(left, true);
            }

            if (MatchKeyword("IN"))
            {
                return ParseInOrSubqueryIn(left, false);
            }

            if (MatchKeyword("BETWEEN"))
            {
                var low = ParseScalarPrimary();
                if (!MatchKeyword("AND"))
                    throw CosmosEmulatorException.BadRequest("BETWEEN requires AND keyword.");
                var high = ParseScalarPrimary();
                return new BinaryBooleanExpression(
                    new ComparisonBooleanExpression(left, ComparisonOperator.GreaterThanOrEqual, low),
                    BooleanOperator.And,
                    new ComparisonBooleanExpression(left, ComparisonOperator.LessThanOrEqual, high));
            }

            if (MatchKeyword("LIKE"))
            {
                var pattern = ParseScalarPrimary();
                return new LikeBooleanExpression(left, pattern);
            }

            if (Current.Type == TokenType.Operator)
            {
                var comparison = ParseComparisonOperator();
                var right = ParseScalarPrimary();
                return new ComparisonBooleanExpression(left, comparison, right);
            }

            return new ScalarBooleanExpression(left);
        }

        private BooleanExpression ParseInOrSubqueryIn(ScalarExpression left, bool negated)
        {
            Expect(TokenType.OpenParen, "(");

            if (Current.Type == TokenType.Identifier
                && string.Equals(Current.Text, "SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var innerQuery = CollectInnerQueryText();
                Expect(TokenType.CloseParen, ")");
                return new SubqueryInBooleanExpression(left, innerQuery, negated);
            }

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
            return new InBooleanExpression(left, values, negated);
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

                // Reconstruct token text, restoring quotes around string literals
                var text = Current.Type == TokenType.String
                    ? "'" + Current.Text.Replace("'", "''") + "'"
                    : Current.Text;
                parts.Add(text);
                _index++;
            }

            return string.Join(" ", parts);
        }

        private ScalarExpression ParseScalarPrimary()
        {
            return Current.Type switch
            {
                TokenType.OpenParen => ParseParenthesizedScalar(),
                TokenType.OpenBracket => ParseArrayLiteral(),
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

        public bool TryConsumeComma() => TryConsume(TokenType.Comma);

        public string? TryConsumeAlias()
        {
            if (!MatchKeyword("AS"))
            {
                return null;
            }

            if (Current.Type != TokenType.Identifier)
            {
                throw CosmosEmulatorException.BadRequest("AS requires an alias name.");
            }

            var alias = Current.Text;
            _index++;
            return alias;
        }

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

                if (current == '-' && index + 1 < text.Length && char.IsDigit(text[index + 1])
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

    private sealed class QueryRow
    {
        public QueryRow(CosmosDocument? document, Dictionary<string, object?> aliases)
        {
            Document = document;
            Aliases = aliases;
        }

        public CosmosDocument? Document { get; }

        public Dictionary<string, object?> Aliases { get; }

        public QueryRow WithAlias(string alias, object? value)
        {
            var aliases = new Dictionary<string, object?>(Aliases, StringComparer.OrdinalIgnoreCase)
            {
                [alias] = value
            };

            return new QueryRow(Document, aliases);
        }
    }

    private sealed class QueryGroup
    {
        public QueryGroup(IReadOnlyList<object?> keyValues, IReadOnlyList<QueryRow> rows)
        {
            KeyValues = keyValues;
            Rows = rows.ToList();
        }

        public IReadOnlyList<object?> KeyValues { get; }

        public List<QueryRow> Rows { get; }
    }

    private sealed record QueryPlan(
        string FromAlias,
        IReadOnlyList<JoinClause> Joins,
        Projection Projection,
        BooleanExpression? Where,
        IReadOnlyList<ScalarExpression> GroupBy,
        IReadOnlyList<OrderByClause> OrderBy,
        int? Top,
        int? Offset,
        int? Limit,
        bool Distinct = false,
        SubqueryFromSource? FromSubquery = null,
        ScalarExpression? ArrayIterationSource = null)
    {
        public bool RequiresAggregation => GroupBy.Count > 0 || Projection.ContainsAggregate;
    }

    private sealed record SubqueryFromSource(string InnerQuery, string Alias);

    private sealed record JoinClause(string Alias, ScalarExpression SourceExpression);

    private sealed record Projection(ProjectionMode Mode, IReadOnlyList<SelectItem> Items)
    {
        public bool ContainsAggregate => Items.Any(item => ContainsAggregateFunction(item.Expression));
    }

    private static bool ContainsAggregateFunction(ScalarExpression expression)
    {
        return expression switch
        {
            FunctionCallExpression function => IsAggregateFunction(function.Name) || function.Arguments.Any(ContainsAggregateFunction),
            _ => false
        };
    }

    private sealed record SelectItem(ScalarExpression Expression, string OutputName);

    private sealed record OrderByClause(ScalarExpression Expression, bool Descending);

    private enum ProjectionMode
    {
        All,
        Fields,
        Value
    }

    private abstract record BooleanExpression;

    private sealed record BinaryBooleanExpression(BooleanExpression Left, BooleanOperator Operator, BooleanExpression Right) : BooleanExpression;

    private sealed record NotBooleanExpression(BooleanExpression Expression) : BooleanExpression;

    private sealed record ComparisonBooleanExpression(ScalarExpression Left, ComparisonOperator Operator, ScalarExpression Right) : BooleanExpression;

    private sealed record InBooleanExpression(ScalarExpression Left, IReadOnlyList<ScalarExpression> Values, bool Negated) : BooleanExpression;

    private sealed record LikeBooleanExpression(ScalarExpression Left, ScalarExpression Pattern) : BooleanExpression;

    private sealed record ScalarBooleanExpression(ScalarExpression Expression) : BooleanExpression;

    private sealed record SubqueryInBooleanExpression(ScalarExpression Left, string InnerQuery, bool Negated) : BooleanExpression;

    private sealed record ExistsBooleanExpression(string InnerQuery) : BooleanExpression;

    private sealed record SubqueryContext(string DatabaseId, string ContainerId);

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
        Comma,
        Operator,
        Asterisk
    }

    private sealed record Token(TokenType Type, string Text);

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

    #region Spatial Functions

    private static object? EvaluateStDistance(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest("ST_DISTANCE expects two arguments.");

        var g1 = SpatialHelper.TryParseGeoJson(arguments[0]);
        var g2 = SpatialHelper.TryParseGeoJson(arguments[1]);
        if (g1 is null || g2 is null)
            return UndefinedValue;

        return SpatialHelper.GeodesicDistanceMeters(g1, g2);
    }

    private static object? EvaluateStWithin(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest("ST_WITHIN expects two arguments.");

        var g1 = SpatialHelper.TryParseGeoJson(arguments[0]);
        var g2 = SpatialHelper.TryParseGeoJson(arguments[1]);
        if (g1 is null || g2 is null)
            return UndefinedValue;

        return g1.Within(g2);
    }

    private static object? EvaluateStIntersects(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 2)
            throw CosmosEmulatorException.BadRequest("ST_INTERSECTS expects two arguments.");

        var g1 = SpatialHelper.TryParseGeoJson(arguments[0]);
        var g2 = SpatialHelper.TryParseGeoJson(arguments[1]);
        if (g1 is null || g2 is null)
            return UndefinedValue;

        return g1.Intersects(g2);
    }

    private static object? EvaluateStIsValid(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("ST_ISVALID expects one argument.");

        var (isValid, _) = SpatialHelper.ValidateGeoJson(arguments[0]);
        return isValid;
    }

    private static object? EvaluateStIsValidDetailed(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("ST_ISVALIDDETAILED expects one argument.");

        var (isValid, reason) = SpatialHelper.ValidateGeoJson(arguments[0]);

        var result = new JsonObject
        {
            ["valid"] = isValid,
            ["reason"] = reason
        };
        return result;
    }

    private static object? EvaluateStArea(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
            throw CosmosEmulatorException.BadRequest("ST_AREA expects one argument.");

        var geometry = SpatialHelper.TryParseGeoJson(arguments[0]);
        if (geometry is null)
            return UndefinedValue;

        return SpatialHelper.GeodesicAreaSquareMeters(geometry);
    }

    #endregion
}
