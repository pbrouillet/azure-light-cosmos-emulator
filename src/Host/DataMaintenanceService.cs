using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.Host;

/// <summary>
/// Periodically trims stale activity log, query telemetry, and change feed entries
/// to prevent unbounded memory growth in the storage backend.
/// </summary>
public sealed class DataMaintenanceService(
    IActivityStore activityStore,
    IQueryTelemetryStore queryTelemetryStore,
    IChangeFeedProvider changeFeedProvider,
    EmulatorOptions options,
    ILogger<DataMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);

        // Run immediately at startup to trim data from prior runs
        await RunMaintenanceAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunMaintenanceAsync(stoppingToken);
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        try
        {
            await activityStore.TrimAsync(options.MaxActivityLogEntries, ct);
            await queryTelemetryStore.TrimAsync(options.MaxQueryTelemetryEntries, ct);
            await changeFeedProvider.TrimAsync(TimeSpan.FromMinutes(options.ChangeFeedRetentionMinutes), ct);

            logger.LogDebug(
                "Data maintenance completed. Limits: activity={ActivityMax}, telemetry={TelemetryMax}, changeFeedRetention={RetentionMin}m",
                options.MaxActivityLogEntries,
                options.MaxQueryTelemetryEntries,
                options.ChangeFeedRetentionMinutes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Data maintenance iteration failed.");
        }
    }
}
