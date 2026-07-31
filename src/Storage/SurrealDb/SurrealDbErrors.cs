using SurrealDb.Net.Models.Response;

namespace Azure.Cosmos.LightEmulator.Storage.SurrealDb;

/// <summary>
/// Helpers for interpreting SurrealDB embedded engine errors.
/// </summary>
/// <remarks>
/// SurrealDB 1.0.0 surfaces an error when a SELECT/DELETE targets a table that has
/// not been created yet, whereas 0.9.0 returned an empty result. Direct
/// <c>client.Select</c> calls throw a <c>SurrealDbEmbeddedException</c>, while
/// <c>client.RawQuery(...).EnsureAllOks()</c> throws a generic
/// <c>ResponseUnsuccessfulException</c> that hides the detail text — so the raw-query
/// path must inspect the response errors directly.
/// </remarks>
public static class SurrealDbErrors
{
    private const string MissingTableMarker = "does not exist";

    /// <summary>
    /// Detects the "table does not exist" error thrown by direct <c>client.Select</c> calls.
    /// </summary>
    public static bool IsMissingTable(Exception ex) =>
        ex.GetType().Name == "SurrealDbEmbeddedException"
        && ex.Message.Contains(MissingTableMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detects a raw-query response whose only errors are "table does not exist".
    /// </summary>
    public static bool IsMissingTable(SurrealDbResponse response)
    {
        if (!response.HasErrors)
        {
            return false;
        }

        var errors = response.Errors.ToList();
        return errors.Count > 0 && errors.All(IsMissingTableError);
    }

    private static bool IsMissingTableError(ISurrealDbErrorResult error)
    {
        var details = error.GetType().GetProperty("Details")?.GetValue(error) as string;
        return details is not null
            && details.Contains(MissingTableMarker, StringComparison.OrdinalIgnoreCase);
    }
}
