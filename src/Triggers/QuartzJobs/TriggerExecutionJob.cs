using Quartz;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.Triggers.QuartzJobs;

/// <summary>
/// Quartz.NET job that executes deferred/scheduled Cosmos DB triggers.
/// </summary>
public class TriggerExecutionJob : IJob
{
    private readonly Engine.TriggerEngine _triggerEngine;
    private readonly ILogger<TriggerExecutionJob> _logger;

    public TriggerExecutionJob(Engine.TriggerEngine triggerEngine, ILogger<TriggerExecutionJob> logger)
    {
        _triggerEngine = triggerEngine;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var dataMap = context.JobDetail.JobDataMap;
        var databaseId = dataMap.GetString("DatabaseId") ?? "";
        var containerId = dataMap.GetString("ContainerId") ?? "";
        var triggerId = dataMap.GetString("TriggerId") ?? "";

        _logger.LogInformation("Executing scheduled trigger {TriggerId} on {DatabaseId}/{ContainerId}",
            triggerId, databaseId, containerId);

        // TODO: Load document context and execute trigger
        await Task.CompletedTask;
    }
}
