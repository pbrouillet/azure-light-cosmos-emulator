using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.NoSql.Query;

/// <summary>
/// Validates whether a query can execute given a container's indexing policy.
/// </summary>
public sealed class IndexValidationService
{
    /// <summary>
    /// Validates a query against the container's indexing policy.
    /// </summary>
    public IndexValidationResult ValidateQuery(
        IndexingPolicy policy,
        IReadOnlyList<string> filterPaths,
        IReadOnlyList<(string path, bool descending)> orderByPaths,
        bool scanEnabled)
    {
        if (policy.IndexingMode == IndexingMode.None)
        {
            if (!scanEnabled)
            {
                return new IndexValidationResult(
                    RequiresScan: true,
                    IsAllowed: false,
                    ErrorMessage: "Queries are not supported when indexing mode is set to None. " +
                                  "Please set the x-ms-documentdb-query-enable-scan header to true.",
                    RuMultiplier: 1.0);
            }

            return new IndexValidationResult(
                RequiresScan: true,
                IsAllowed: true,
                ErrorMessage: null,
                RuMultiplier: 3.0);
        }

        foreach (var filterPath in filterPaths)
        {
            if (!IsIndexed(filterPath, policy))
            {
                if (!scanEnabled)
                {
                    return new IndexValidationResult(
                        RequiresScan: true,
                        IsAllowed: false,
                        ErrorMessage: $"The query filter on path '{filterPath}' requires a scan because it is excluded " +
                                      "from indexing. Please set the x-ms-documentdb-query-enable-scan header to true.",
                        RuMultiplier: 1.0);
                }

                return new IndexValidationResult(
                    RequiresScan: true,
                    IsAllowed: true,
                    ErrorMessage: null,
                    RuMultiplier: 2.0);
            }
        }

        if (orderByPaths.Count >= 2)
        {
            if (!HasMatchingCompositeIndex(policy, orderByPaths))
            {
                return new IndexValidationResult(
                    RequiresScan: false,
                    IsAllowed: false,
                    ErrorMessage: "The order by query does not have a corresponding composite index that it can be served from.",
                    RuMultiplier: 1.0);
            }
        }

        return new IndexValidationResult(
            RequiresScan: false,
            IsAllowed: true,
            ErrorMessage: null,
            RuMultiplier: 1.0);
    }

    internal static bool IsIndexed(string path, IndexingPolicy policy)
    {
        if (policy.IndexingMode == IndexingMode.None)
        {
            return false;
        }

        if (policy.ExcludedPaths.Any(excluded => PathMatches(excluded.Path, path)))
        {
            return false;
        }

        return policy.IncludedPaths.Count == 0
            || policy.IncludedPaths.Any(included => PathMatches(included.Path, path));
    }

    internal static bool PathMatches(string configuredPath, string candidatePath)
    {
        var normalizedConfigured = NormalizePolicyPath(configuredPath);
        var normalizedCandidate = NormalizePolicyPath(candidatePath);

        return normalizedConfigured.Equals("/*", StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.Equals(normalizedConfigured, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedConfigured.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizePolicyPath(string path)
    {
        return path
            .Replace("/?", string.Empty, StringComparison.Ordinal)
            .Replace("/*", "/*", StringComparison.Ordinal)
            .Replace('"', ' ')
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    internal static bool HasMatchingCompositeIndex(
        IndexingPolicy policy,
        IReadOnlyList<(string path, bool descending)> orderByPaths)
    {
        if (policy.CompositeIndexes is null)
        {
            return false;
        }

        return policy.CompositeIndexes.Any(index =>
        {
            if (index.Paths.Count != orderByPaths.Count)
            {
                return false;
            }

            for (var i = 0; i < orderByPaths.Count; i++)
            {
                var normalizedIndexPath = NormalizePolicyPath(index.Paths[i].Path);
                var normalizedQueryPath = NormalizePolicyPath(orderByPaths[i].path);
                var expectedOrder = orderByPaths[i].descending ? SortOrder.Descending : SortOrder.Ascending;

                if (!normalizedIndexPath.Equals(normalizedQueryPath, StringComparison.OrdinalIgnoreCase)
                    || index.Paths[i].Order != expectedOrder)
                {
                    return false;
                }
            }

            return true;
        });
    }

    /// <summary>
    /// Converts a query-style path (e.g. "c.name" or "c.address.city") to an indexing-policy path (e.g. "/name" or "/address/city").
    /// </summary>
    internal static string? ConvertToIndexPath(string? queryPath)
    {
        if (string.IsNullOrWhiteSpace(queryPath))
        {
            return null;
        }

        var path = queryPath.Trim();

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
}

/// <summary>
/// Result of index validation for a query.
/// </summary>
public sealed record IndexValidationResult(
    bool RequiresScan,
    bool IsAllowed,
    string? ErrorMessage,
    double RuMultiplier);
