using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class TtlCleanupService(
    IDocumentStore documentStore,
    IConsistencyManager consistencyManager,
    RuTracker ruTracker,
    ILogger<TtlCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);

        await RunCleanupAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCleanupAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is ObjectDisposedException))
        {
            // The SurrealDB embedded client creates linked CancellationTokenSource instances
            // from the stoppingToken for each database operation. A race condition between
            // CTS disposal and callback execution causes ObjectDisposedException when the
            // stopping token is canceled during shutdown. This is benign — the service is
            // stopping and no data integrity is at risk.
            logger.LogDebug("Suppressed CancellationTokenSource disposal race during shutdown.");
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var databases = await documentStore.ListDatabasesAsync(ct);

            foreach (var database in databases.Resources)
            {
                var containers = await documentStore.ListContainersAsync(database.Id, ct);
                foreach (var container in containers.Resources.Where(HasEnabledTtl))
                {
                    var documents = await documentStore.ListDocumentsAsync(database.Id, container.Id, ct);
                    foreach (var document in documents.Resources)
                    {
                        var ttl = ResolveEffectiveTtl(document, container);
                        if (ttl is null || ttl <= 0 || document.Timestamp + ttl.Value >= now)
                        {
                            continue;
                        }

                        await documentStore.DeleteDocumentAsync(database.Id, container.Id, document.Id, document.PartitionKey, ct);
                        var lsn = await documentStore.GetGlobalLsnAsync(ct);
                        consistencyManager.GenerateSessionToken(database.Id, container.Id, lsn);
                        ruTracker.RecordRequest(
                            0,
                            method: "TTL",
                            path: $"/dbs/{database.Id}/colls/{container.Id}/docs/{document.Id}",
                            statusCode: 204,
                            latencyMs: 0,
                            databaseId: database.Id,
                            containerId: container.Id);
                        logger.LogInformation(
                            "Deleted expired document {DocumentId} from {DatabaseId}/{ContainerId} via TTL enforcement.",
                            document.Id,
                            database.Id,
                            container.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TTL cleanup iteration failed.");
        }
    }

    private static bool HasEnabledTtl(CosmosContainer container) => container.DefaultTimeToLive is > 0;

    private static int? ResolveEffectiveTtl(CosmosDocument document, CosmosContainer container) => document.TimeToLive switch
    {
        > 0 => document.TimeToLive,
        -1 => null,
        0 => container.DefaultTimeToLive,
        null => container.DefaultTimeToLive,
        _ => null
    };
}
