using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Jint;
using Jint.Native;

namespace Azure.Cosmos.LightEmulator.Triggers.Engine;

/// <summary>
/// Engine for evaluating pre/post triggers on document operations.
/// Uses Jint JavaScript interpreter for trigger body execution.
/// </summary>
public class TriggerEngine
{
    private static readonly TimeSpan TriggerTimeout = TimeSpan.FromSeconds(5);
    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private readonly IProgrammabilityEngine _programmability;

    public TriggerEngine(IProgrammabilityEngine programmability)
    {
        _programmability = programmability;
    }

    /// <summary>
    /// Executes pre-triggers before a document operation.
    /// Pre-triggers can modify the document before it's written.
    /// </summary>
    public async Task<JsonObject> ExecutePreTriggersAsync(
        string databaseId,
        string containerId,
        JsonObject document,
        TriggerOperation operation,
        IEnumerable<string> triggerIds,
        CancellationToken ct = default)
    {
        var currentDoc = document;
        foreach (var triggerId in triggerIds)
        {
            ct.ThrowIfCancellationRequested();

            var trigger = await _programmability.GetTriggerAsync(databaseId, containerId, triggerId, ct);
            if (trigger.TriggerType != TriggerType.Pre)
                throw CosmosEmulatorException.BadRequest($"Trigger '{triggerId}' is not a pre-trigger.");
            if (trigger.TriggerOperation != TriggerOperation.All && trigger.TriggerOperation != operation)
                continue;

            currentDoc = ExecuteTriggerBody(trigger, currentDoc, isPreTrigger: true);
        }
        return currentDoc;
    }

    /// <summary>
    /// Executes post-triggers after a document operation.
    /// </summary>
    public async Task ExecutePostTriggersAsync(
        string databaseId,
        string containerId,
        CosmosDocument document,
        TriggerOperation operation,
        IEnumerable<string> triggerIds,
        CancellationToken ct = default)
    {
        foreach (var triggerId in triggerIds)
        {
            ct.ThrowIfCancellationRequested();

            var trigger = await _programmability.GetTriggerAsync(databaseId, containerId, triggerId, ct);
            if (trigger.TriggerType != TriggerType.Post)
                throw CosmosEmulatorException.BadRequest($"Trigger '{triggerId}' is not a post-trigger.");
            if (trigger.TriggerOperation != TriggerOperation.All && trigger.TriggerOperation != operation)
                continue;

            ExecuteTriggerBody(trigger, document.ToResponseBody(), isPreTrigger: false);
        }
    }

    private static JsonObject ExecuteTriggerBody(Trigger trigger, JsonObject document, bool isPreTrigger)
    {
        var engine = new Jint.Engine(options => options.TimeoutInterval(TriggerTimeout));

        var requestBody = document.DeepClone().AsObject();
        var responseBody = document.DeepClone().AsObject();

        engine.SetValue("getContext", new Func<object>(() => new
        {
            getRequest = new Func<object>(() => new
            {
                getBody = new Func<object?>(() => JsValue.FromObject(engine, JsonNode.Parse(requestBody.ToJsonString()))),
                setBody = new Action<JsValue>(value =>
                {
                    var jsonStr = value.IsString()
                        ? value.AsString()
                        : System.Text.Json.JsonSerializer.Serialize(value.ToObject(), s_jsonOptions);
                    var json = JsonNode.Parse(jsonStr)?.AsObject();
                    if (json is not null)
                    {
                        requestBody = json;
                    }
                })
            }),
            getResponse = new Func<object>(() => new
            {
                getBody = new Func<object?>(() => JsValue.FromObject(engine, JsonNode.Parse(responseBody.ToJsonString()))),
                setBody = new Action<JsValue>(value =>
                {
                    var jsonStr = value.IsString()
                        ? value.AsString()
                        : System.Text.Json.JsonSerializer.Serialize(value.ToObject(), s_jsonOptions);
                    var json = JsonNode.Parse(jsonStr)?.AsObject();
                    if (json is not null)
                    {
                        responseBody = json;
                    }
                })
            }),
            getCollection = new Func<object>(() => new
            {
                getSelfLink = new Func<string>(() => $"dbs/{trigger.DatabaseId}/colls/{trigger.ContainerId}/")
            })
        }));

        try
        {
            engine.Execute($"var __triggerFn = {trigger.Body}; __triggerFn();");
        }
        catch (TimeoutException)
        {
            throw CosmosEmulatorException.RequestTimeout(
                $"Trigger '{trigger.Id}' execution exceeded the maximum allowed time.");
        }
        catch (CosmosEmulatorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CosmosEmulatorException.BadRequest($"Trigger execution failed: {ex.Message}");
        }

        return isPreTrigger ? requestBody : responseBody;
    }
}
