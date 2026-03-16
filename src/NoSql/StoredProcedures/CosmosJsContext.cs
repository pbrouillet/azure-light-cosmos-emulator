using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Jint;
using Jint.Native;

namespace Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;

/// <summary>
/// Exposes a Cosmos DB-style JavaScript execution context to stored procedures.
/// </summary>
public sealed class CosmosJsContext
{
    private readonly IDocumentStore _store;
    private readonly IQueryEngine _queryEngine;
    private readonly string _databaseId;
    private readonly string _containerId;
    private readonly PartitionKeyValue _partitionKey;
    private readonly CancellationToken _ct;
    private readonly CollectionObject _collection;
    private readonly ResponseObject _response;
    private readonly RequestObject _request;
    private Engine? _engine;
    private object? _responseBody;
    private object? _requestBody;

    public CosmosJsContext(
        IDocumentStore store,
        IQueryEngine queryEngine,
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        CancellationToken ct = default)
    {
        _store = store;
        _queryEngine = queryEngine;
        _databaseId = databaseId;
        _containerId = containerId;
        _partitionKey = partitionKey;
        _ct = ct;
        _collection = new CollectionObject(this);
        _response = new ResponseObject(this);
        _request = new RequestObject(this);
    }

    public void Bind(Engine engine) => _engine = engine;

    public CosmosJsContext getContext() => this;

    public CollectionObject getCollection() => _collection;

    public ResponseObject getResponse() => _response;

    public RequestObject getRequest() => _request;

    internal string CollectionSelfLink => $"dbs/{_databaseId}/colls/{_containerId}/";

    internal object? ResponseBody
    {
        get => _responseBody;
        set => _responseBody = value;
    }

    internal object? RequestBody
    {
        get => _requestBody;
        set => _requestBody = value;
    }

    internal CosmosDocument CreateDocument(JsValue document)
    {
        var doc = ToJsonObject(document, "doc");
        return _store.CreateDocumentAsync(_databaseId, _containerId, doc, _ct).GetAwaiter().GetResult();
    }

    internal CosmosDocument ReadDocument(string documentLink)
    {
        var documentId = ExtractDocumentId(documentLink);
        return _store.ReadDocumentAsync(_databaseId, _containerId, documentId, _partitionKey, _ct).GetAwaiter().GetResult();
    }

    internal CosmosDocument ReplaceDocument(string documentLink, JsValue document)
    {
        var documentId = ExtractDocumentId(documentLink);
        var doc = ToJsonObject(document, "doc");
        return _store.ReplaceDocumentAsync(_databaseId, _containerId, documentId, doc, ct: _ct).GetAwaiter().GetResult();
    }

    internal void DeleteDocument(string documentLink)
    {
        var documentId = ExtractDocumentId(documentLink);
        _store.DeleteDocumentAsync(_databaseId, _containerId, documentId, _partitionKey, _ct).GetAwaiter().GetResult();
    }

    internal IReadOnlyList<JsonObject> QueryDocuments(JsValue query)
    {
        var (queryText, parameters) = ExtractQueryDefinition(query);
        var result = _queryEngine.ExecuteQueryAsync(
            _databaseId,
            _containerId,
            queryText,
            parameters,
            new QueryOptions
            {
                PartitionKey = _partitionKey
            },
            _ct).GetAwaiter().GetResult();

        return result.Resources
            .Select(document => document.DeepClone().AsObject())
            .ToList();
    }

    internal void InvokeCallback(JsValue callback, params object?[] args)
    {
        if (callback == JsValue.Undefined || callback == JsValue.Null)
        {
            return;
        }

        var engine = _engine ?? throw new InvalidOperationException("Jint engine has not been bound to the Cosmos JS context.");
        var jsArgs = args.Select(arg => JsValue.FromObject(engine, arg)).ToArray();
        engine.Call(callback, JsValue.Undefined, jsArgs);
    }

    internal static object? ToHostObject(JsValue value)
    {
        if (value == JsValue.Null || value == JsValue.Undefined)
        {
            return null;
        }

        return value.ToObject();
    }

    internal static object CreateErrorObject(Exception ex) => ex switch
    {
        CosmosEmulatorException cosmosEx => new { code = cosmosEx.ErrorCode, message = cosmosEx.Message },
        _ => new { code = "BadRequest", message = ex.Message }
    };

    private static JsonObject ToJsonObject(JsValue value, string argumentName)
    {
        var hostObject = ToHostObject(value);
        if (hostObject is null)
        {
            throw CosmosEmulatorException.BadRequest($"'{argumentName}' must be a JSON object.");
        }

        if (hostObject is JsonObject jsonObject)
        {
            return jsonObject.DeepClone().AsObject();
        }

        if (hostObject is JsonNode jsonNode)
        {
            if (jsonNode is JsonObject nodeObject)
            {
                return nodeObject.DeepClone().AsObject();
            }

            throw CosmosEmulatorException.BadRequest($"'{argumentName}' must be a JSON object.");
        }

        var serialized = JsonSerializer.Serialize(hostObject);
        var parsed = JsonNode.Parse(serialized) as JsonObject;
        if (parsed is null)
        {
            throw CosmosEmulatorException.BadRequest($"'{argumentName}' must be a JSON object.");
        }

        return parsed;
    }

    private static (string QueryText, IReadOnlyDictionary<string, object?> Parameters) ExtractQueryDefinition(JsValue query)
    {
        var hostObject = ToHostObject(query);
        return hostObject switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => (text, new Dictionary<string, object?>()),
            JsonObject jsonObject when jsonObject["query"] is JsonValue jsonValue =>
                (jsonValue.GetValue<string>(), ExtractQueryParameters(jsonObject["parameters"])),
            IDictionary<string, object?> dictionary when dictionary.TryGetValue("query", out var value) && value is string text && !string.IsNullOrWhiteSpace(text) =>
                (text, ExtractQueryParameters(dictionary.TryGetValue("parameters", out var parameters) ? parameters : null)),
            _ => throw CosmosEmulatorException.BadRequest("'query' must be a string or an object with a 'query' property.")
        };
    }

    private static IReadOnlyDictionary<string, object?> ExtractQueryParameters(object? parameters)
    {
        var values = new Dictionary<string, object?>();

        switch (parameters)
        {
            case null:
                return values;
            case JsonArray jsonArray:
                foreach (var entry in jsonArray)
                {
                    TryAddParameter(values, entry);
                }
                return values;
            case IEnumerable<object?> enumerable:
                foreach (var entry in enumerable)
                {
                    TryAddParameter(values, entry);
                }
                return values;
            default:
                return values;
        }
    }

    private static void TryAddParameter(IDictionary<string, object?> parameters, object? entry)
    {
        switch (entry)
        {
            case JsonObject jsonObject when jsonObject["name"]?.GetValue<string>() is { Length: > 0 } name:
                parameters[name] = jsonObject["value"];
                break;
            case IDictionary<string, object?> dictionary when dictionary.TryGetValue("name", out var nameValue)
                && nameValue is string dictionaryName
                && !string.IsNullOrWhiteSpace(dictionaryName):
                parameters[dictionaryName] = dictionary.TryGetValue("value", out var value) ? value : null;
                break;
        }
    }

    private static string ExtractDocumentId(string documentLink)
    {
        if (string.IsNullOrWhiteSpace(documentLink))
        {
            throw CosmosEmulatorException.BadRequest("Document link must be provided.");
        }

        var trimmed = documentLink.Trim();
        var docsSegment = "/docs/";
        var docsIndex = trimmed.IndexOf(docsSegment, StringComparison.OrdinalIgnoreCase);
        if (docsIndex >= 0)
        {
            var suffix = trimmed[(docsIndex + docsSegment.Length)..].Trim('/');
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                return suffix;
            }
        }

        var parts = trimmed.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw CosmosEmulatorException.BadRequest("Document link must be provided.");
        }

        return parts[^1];
    }

    public sealed class CollectionObject
    {
        private readonly CosmosJsContext _context;

        internal CollectionObject(CosmosJsContext context)
        {
            _context = context;
        }

        public string getSelfLink() => _context.CollectionSelfLink;

        public bool createDocument(string selfLink, JsValue doc, JsValue options, JsValue callback)
        {
            CosmosDocument created;
            try
            {
                created = _context.CreateDocument(doc);
            }
            catch (Exception ex)
            {
                _context.InvokeCallback(callback, CosmosJsContext.CreateErrorObject(ex), null, CosmosJsContext.ToHostObject(options));
                return false;
            }

            _context.InvokeCallback(callback, null, created.ToResponseBody(), CosmosJsContext.ToHostObject(options));
            return true;
        }

        public bool readDocument(string docLink, JsValue options, JsValue callback)
        {
            CosmosDocument document;
            try
            {
                document = _context.ReadDocument(docLink);
            }
            catch (Exception ex)
            {
                _context.InvokeCallback(callback, CosmosJsContext.CreateErrorObject(ex), null, CosmosJsContext.ToHostObject(options));
                return false;
            }

            _context.InvokeCallback(callback, null, document.ToResponseBody(), CosmosJsContext.ToHostObject(options));
            return true;
        }

        public bool replaceDocument(string docLink, JsValue doc, JsValue options, JsValue callback)
        {
            CosmosDocument document;
            try
            {
                document = _context.ReplaceDocument(docLink, doc);
            }
            catch (Exception ex)
            {
                _context.InvokeCallback(callback, CosmosJsContext.CreateErrorObject(ex), null, CosmosJsContext.ToHostObject(options));
                return false;
            }

            _context.InvokeCallback(callback, null, document.ToResponseBody(), CosmosJsContext.ToHostObject(options));
            return true;
        }

        public bool deleteDocument(string docLink, JsValue options, JsValue callback)
        {
            try
            {
                _context.DeleteDocument(docLink);
            }
            catch (Exception ex)
            {
                _context.InvokeCallback(callback, CosmosJsContext.CreateErrorObject(ex), null, CosmosJsContext.ToHostObject(options));
                return false;
            }

            _context.InvokeCallback(callback, null, null, CosmosJsContext.ToHostObject(options));
            return true;
        }

        public bool queryDocuments(string selfLink, JsValue query, JsValue options, JsValue callback)
        {
            IReadOnlyList<JsonObject> documents;
            try
            {
                documents = _context.QueryDocuments(query);
            }
            catch (Exception ex)
            {
                _context.InvokeCallback(callback, CosmosJsContext.CreateErrorObject(ex), null, null);
                return false;
            }

            _context.InvokeCallback(callback, null, documents, new { count = documents.Count, options = CosmosJsContext.ToHostObject(options) });
            return true;
        }
    }

    public sealed class ResponseObject
    {
        private readonly CosmosJsContext _context;

        internal ResponseObject(CosmosJsContext context)
        {
            _context = context;
        }

        public void setBody(JsValue body) => _context.ResponseBody = CosmosJsContext.ToHostObject(body);

        public object? getBody() => _context.ResponseBody;

        public void appendBody(JsValue body)
        {
            var value = CosmosJsContext.ToHostObject(body);
            if (_context.ResponseBody is null)
            {
                _context.ResponseBody = value;
                return;
            }

            _context.ResponseBody = string.Concat(SerializeForAppend(_context.ResponseBody), SerializeForAppend(value));
        }

        private static string SerializeForAppend(object? value) => value switch
        {
            null => string.Empty,
            string text => text,
            _ => JsonSerializer.Serialize(value)
        };
    }

    public sealed class RequestObject
    {
        private readonly CosmosJsContext _context;

        internal RequestObject(CosmosJsContext context)
        {
            _context = context;
        }

        public object? getBody() => _context.RequestBody;

        public void setBody(JsValue body) => _context.RequestBody = CosmosJsContext.ToHostObject(body);
    }
}
