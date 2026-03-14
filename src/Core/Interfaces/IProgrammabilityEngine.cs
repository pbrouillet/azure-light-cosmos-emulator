using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Engine for executing stored procedures, triggers, and UDFs.
/// </summary>
public interface IProgrammabilityEngine
{
    // Stored procedures
    Task<StoredProcedure> CreateStoredProcedureAsync(string databaseId, string containerId, StoredProcedure sproc, CancellationToken ct = default);
    Task<StoredProcedure> GetStoredProcedureAsync(string databaseId, string containerId, string sprocId, CancellationToken ct = default);
    Task<FeedResponse<StoredProcedure>> ListStoredProceduresAsync(string databaseId, string containerId, CancellationToken ct = default);
    Task<StoredProcedure> ReplaceStoredProcedureAsync(string databaseId, string containerId, StoredProcedure sproc, CancellationToken ct = default);
    Task DeleteStoredProcedureAsync(string databaseId, string containerId, string sprocId, CancellationToken ct = default);
    Task<object?> ExecuteStoredProcedureAsync(string databaseId, string containerId, string sprocId, object?[] args, PartitionKeyValue partitionKey, CancellationToken ct = default);

    // Triggers
    Task<Trigger> CreateTriggerAsync(string databaseId, string containerId, Trigger trigger, CancellationToken ct = default);
    Task<Trigger> GetTriggerAsync(string databaseId, string containerId, string triggerId, CancellationToken ct = default);
    Task<FeedResponse<Trigger>> ListTriggersAsync(string databaseId, string containerId, CancellationToken ct = default);
    Task<Trigger> ReplaceTriggerAsync(string databaseId, string containerId, Trigger trigger, CancellationToken ct = default);
    Task DeleteTriggerAsync(string databaseId, string containerId, string triggerId, CancellationToken ct = default);

    // User-defined functions
    Task<UserDefinedFunction> CreateUdfAsync(string databaseId, string containerId, UserDefinedFunction udf, CancellationToken ct = default);
    Task<UserDefinedFunction> GetUdfAsync(string databaseId, string containerId, string udfId, CancellationToken ct = default);
    Task<FeedResponse<UserDefinedFunction>> ListUdfsAsync(string databaseId, string containerId, CancellationToken ct = default);
    Task<UserDefinedFunction> ReplaceUdfAsync(string databaseId, string containerId, UserDefinedFunction udf, CancellationToken ct = default);
    Task DeleteUdfAsync(string databaseId, string containerId, string udfId, CancellationToken ct = default);
}
