using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.NoSql.Query;

/// <summary>
/// Accepts query telemetry entries for asynchronous, best-effort persistence.
/// </summary>
public interface IQueryTelemetryRecorder
{
    /// <summary>
    /// Queues a telemetry entry for background persistence. Non-blocking; the entry is
    /// silently dropped if the internal queue is saturated.
    /// </summary>
    void Record(QueryTelemetryEntry entry);
}

/// <summary>
/// Background query-telemetry recorder backed by a bounded channel drained by a single
/// consumer. This replaces per-request fire-and-forget <c>Task.Run</c> persistence, which
/// could spawn an unbounded backlog of detached tasks (each capturing a full telemetry
/// entry with SQL text and serialized query plan) faster than the single-writer storage
/// backend could drain them — the primary source of unbounded memory growth under load.
/// </summary>
public sealed class QueryTelemetryRecorder : IQueryTelemetryRecorder, IAsyncDisposable
{
    private const int MaxQueueDepth = 4096;

    private static readonly JsonSerializerOptions s_jsonOptions =
        new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private readonly IQueryTelemetryStore _store;
    private readonly QueryExplainService _explainService;
    private readonly ILogger<QueryTelemetryRecorder> _logger;
    private readonly Channel<QueryTelemetryEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;

    public QueryTelemetryRecorder(
        IQueryTelemetryStore store,
        QueryExplainService explainService,
        ILogger<QueryTelemetryRecorder> logger)
    {
        _store = store;
        _explainService = explainService;
        _logger = logger;
        _channel = Channel.CreateBounded<QueryTelemetryEntry>(new BoundedChannelOptions(MaxQueueDepth)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    public void Record(QueryTelemetryEntry entry) => _channel.Writer.TryWrite(entry);

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                await ProcessAsync(entry).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    private async Task ProcessAsync(QueryTelemetryEntry entry)
    {
        // Compute the query plan for successful queries only, mirroring the previous
        // behaviour where failed queries were recorded without a plan.
        if (entry.StatusCode == 200 && !string.IsNullOrWhiteSpace(entry.SqlText))
        {
            try
            {
                var explain = await _explainService
                    .ExplainAsync(entry.DatabaseId, entry.ContainerId, entry.SqlText, _cts.Token)
                    .ConfigureAwait(false);
                entry.QueryPlan = JsonSerializer.Serialize(explain, s_jsonOptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Graceful degradation: record telemetry without a plan if explain fails.
            }
        }

        try
        {
            await _store.RecordAsync(entry, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to persist query telemetry entry.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown errors.
        }
        _cts.Dispose();
    }
}
