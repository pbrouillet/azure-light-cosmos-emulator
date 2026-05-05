using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Azure.Cosmos.LightEmulator.Host;

/// <summary>
/// Periodically trims stale activity log, query telemetry, and change feed entries
/// to prevent unbounded memory growth in the storage backend.
/// </summary>
public sealed class DataMaintenanceService(
    IActivityStore activityStore,
    IQueryTelemetryStore queryTelemetryStore,
    IChangeFeedProvider changeFeedProvider,
    IOptions<EmulatorOptions> options,
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
            var opts = options.Value;
            await activityStore.TrimAsync(opts.MaxActivityLogEntries, ct);
            await queryTelemetryStore.TrimAsync(opts.MaxQueryTelemetryEntries, ct);
            await changeFeedProvider.TrimAsync(TimeSpan.FromMinutes(opts.ChangeFeedRetentionMinutes), ct);

            logger.LogDebug(
                "Data maintenance completed. Limits: activity={ActivityMax}, telemetry={TelemetryMax}, changeFeedRetention={RetentionMin}m",
                opts.MaxActivityLogEntries,
                opts.MaxQueryTelemetryEntries,
                opts.ChangeFeedRetentionMinutes);
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
