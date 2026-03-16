using System.Globalization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Azure.Cosmos.LightEmulator.NoSql.Infrastructure;

public sealed record CosmosResponseHeaderOptions
{
    public double RequestCharge { get; init; } = 1.0;
    public string? ActivityId { get; init; }
    public string? DatabaseId { get; init; }
    public string? ContainerId { get; init; }
    public long? ItemLsn { get; init; }
    public bool IncludeSessionToken { get; init; }
    public long? SessionLsn { get; init; }
}

public sealed class CosmosResponseHeaderService(
    IDocumentStore documentStore,
    IProgrammabilityEngine programmabilityEngine,
    IConsistencyManager consistencyManager,
    EmulatorRuntimeState runtimeState)
{
    private const string ResourceQuotaValue = "databases=100;collections=25;storedProcedures=100;triggers=25;functions=25;documentSize=10240";

    public async Task ApplyAsync(HttpResponse response, CosmosResponseHeaderOptions options, CancellationToken ct = default)
    {
        var globalLsn = await documentStore.GetGlobalLsnAsync(ct);
        var resourceUsage = await BuildResourceUsageAsync(ct);

        response.Headers[CosmosHeaders.RequestCharge] = options.RequestCharge.ToString("F2", CultureInfo.InvariantCulture);
        response.Headers[CosmosHeaders.ActivityId] = options.ActivityId ?? Guid.NewGuid().ToString();
        response.Headers[CosmosHeaders.ServiceVersion] = CosmosHeaders.CurrentServiceVersion;
        response.Headers[CosmosHeaders.SchemaVersion] = CosmosHeaders.CurrentSchemaVersion;
        response.Headers[CosmosHeaders.ResourceQuota] = ResourceQuotaValue;
        response.Headers[CosmosHeaders.ResourceUsage] = resourceUsage;
        response.Headers[CosmosHeaders.GlobalCommittedLsn] = globalLsn.ToString(CultureInfo.InvariantCulture);
        response.Headers[CosmosHeaders.CosmosLlsn] = globalLsn.ToString(CultureInfo.InvariantCulture);
        response.Headers[CosmosHeaders.LastStateChangeUtc] = runtimeState.StartedAtUtc.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

        if (options.ItemLsn.HasValue)
        {
            response.Headers[CosmosHeaders.CosmosItemLsn] = options.ItemLsn.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (options.IncludeSessionToken
            && !string.IsNullOrWhiteSpace(options.DatabaseId)
            && !string.IsNullOrWhiteSpace(options.ContainerId))
        {
            var sessionToken = options.SessionLsn.HasValue
                ? consistencyManager.GenerateSessionToken(options.DatabaseId, options.ContainerId, options.SessionLsn.Value)
                : consistencyManager.GetCurrentSessionToken(options.DatabaseId, options.ContainerId);
            response.Headers[CosmosHeaders.SessionToken] = sessionToken;
        }
    }

    private async Task<string> BuildResourceUsageAsync(CancellationToken ct)
    {
        var databases = await documentStore.ListDatabasesAsync(ct);
        var collectionCount = 0;
        var storedProcedureCount = 0;
        var triggerCount = 0;
        var functionCount = 0;

        foreach (var database in databases.Resources)
        {
            var containers = await documentStore.ListContainersAsync(database.Id, ct);
            collectionCount += containers.Count;

            foreach (var container in containers.Resources)
            {
                storedProcedureCount += (await programmabilityEngine.ListStoredProceduresAsync(database.Id, container.Id, ct)).Count;
                triggerCount += (await programmabilityEngine.ListTriggersAsync(database.Id, container.Id, ct)).Count;
                functionCount += (await programmabilityEngine.ListUdfsAsync(database.Id, container.Id, ct)).Count;
            }
        }

        return $"databases={databases.Count};collections={collectionCount};storedProcedures={storedProcedureCount};triggers={triggerCount};functions={functionCount};documentSize=0";
    }
}
