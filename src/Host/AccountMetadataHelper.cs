using Microsoft.AspNetCore.Http;

namespace Azure.Cosmos.LightEmulator.Host;

/// <summary>
/// Builds the Cosmos DB DatabaseAccount JSON response returned on GET / and HEAD /.
/// Shared between <see cref="Program"/> and <see cref="HostApplication"/> startup paths.
/// </summary>
internal static class AccountMetadataHelper
{
    /// <summary>
    /// Query-engine capability flags surfaced in the account metadata so SDKs know
    /// which SQL features the service supports.
    /// </summary>
    internal const string QueryEngineConfiguration =
        "{\"maxSqlQueryInputLength\":262144,\"maxJoinsPerSqlQuery\":5,\"maxLogicalAndPerSqlQuery\":500,\"maxLogicalOrPerSqlQuery\":500,\"maxUdfRefPerSqlQuery\":10,\"maxInExpressionItemsCount\":16000,\"queryMaxInMemorySortDocumentCount\":500,\"maxQueryRequestTimeoutFraction\":0.9,\"sqlAllowNonFiniteNumbers\":false,\"sqlAllowAggregateFunctions\":true,\"sqlAllowSubQuery\":true,\"sqlAllowScalarSubQuery\":true,\"allowNewKeywords\":true,\"sqlAllowLike\":true,\"sqlAllowGroupByClause\":true,\"maxSpatialQueryCells\":12,\"spatialMaxGeometryPointCount\":256,\"sqlDisableOptimizationFlags\":0,\"sqlAllowTop\":true,\"enableSpatialIndexing\":true}";

    /// <summary>
    /// Creates the anonymous object returned as the account metadata response body.
    /// The shape matches the Azure Cosmos DB DatabaseAccount REST response so that
    /// all SDKs (including <c>azure_data_cosmos</c> Rust SDK v0.32+) can deserialize
    /// the <c>writableLocations</c> / <c>readableLocations</c> and other fields they
    /// expect during <c>CosmosClient::build()</c>.
    /// </summary>
    internal static object CreateAccountResponse(HttpContext context, string consistencyLevel = "Session")
    {
        var endpoint = $"{context.Request.Scheme}://{context.Request.Host}/";
        var location = new
        {
            name = "Local",
            databaseAccountEndpoint = endpoint
        };

        return new
        {
            _self = string.Empty,
            id = context.Request.Host.Host,
            _rid = context.Request.Host.Host,
            media = "/media/",
            addresses = "/addresses/",
            _dbs = "/dbs/",
            writableLocations = new[] { location },
            readableLocations = new[] { location },
            enableMultipleWriteLocations = false,
            userReplicationPolicy = new
            {
                asyncReplication = false,
                minReplicaSetSize = 1,
                maxReplicasetSize = 4
            },
            userConsistencyPolicy = new
            {
                defaultConsistencyLevel = consistencyLevel
            },
            systemReplicationPolicy = new
            {
                minReplicaSetSize = 1,
                maxReplicasetSize = 4
            },
            readPolicy = new
            {
                primaryReadCoefficient = 1,
                secondaryReadCoefficient = 1
            },
            queryEngineConfiguration = QueryEngineConfiguration
        };
    }
}
