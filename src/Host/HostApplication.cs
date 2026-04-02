using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Consistency;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Azure.Cosmos.LightEmulator.Kql;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Azure.Cosmos.LightEmulator.NoSql.Middleware;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;
using Azure.Cosmos.LightEmulator.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Azure.Cosmos.LightEmulator.Host;

public static class HostApplication
{
    private const string QueryEngineConfiguration = "{\"maxSqlQueryInputLength\":262144,\"maxJoinsPerSqlQuery\":5,\"maxLogicalAndPerSqlQuery\":500,\"maxLogicalOrPerSqlQuery\":500,\"maxUdfRefPerSqlQuery\":10,\"maxInExpressionItemsCount\":16000,\"queryMaxInMemorySortDocumentCount\":500,\"maxQueryRequestTimeoutFraction\":0.9,\"sqlAllowNonFiniteNumbers\":false,\"sqlAllowAggregateFunctions\":true,\"sqlAllowSubQuery\":true,\"sqlAllowScalarSubQuery\":true,\"allowNewKeywords\":true,\"sqlAllowLike\":true,\"sqlAllowGroupByClause\":true,\"maxSpatialQueryCells\":12,\"spatialMaxGeometryPointCount\":256,\"sqlDisableOptimizationFlags\":0,\"sqlAllowTop\":true,\"enableSpatialIndexing\":true}";

    public static WebApplicationBuilder CreateBuilder(string[]? args = null) =>
        WebApplication.CreateBuilder(args ?? []);

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var emulatorOptions = builder.Configuration.GetSection(EmulatorOptions.SectionName).Get<EmulatorOptions>() ?? new EmulatorOptions();

        ConfigureServices(builder.Services, emulatorOptions);

        var app = builder.Build();
        ConfigurePipeline(app, emulatorOptions);
        return app;
    }

    private static void ConfigureServices(IServiceCollection services, EmulatorOptions emulatorOptions)
    {
        services.AddSingleton(emulatorOptions);
        services.AddSingleton<IOptions<EmulatorOptions>>(_ => Options.Create(emulatorOptions));
        var storageType = StorageServiceRegistration.ParseStorageType(emulatorOptions.Storage);
        services.AddEmulatorStorage(storageType, emulatorOptions.DataDirectory);
        services.AddSingleton<EmulatorRuntimeState>();
        services.AddSingleton<EmulatorAdminSettingsStore>();
        services.AddSingleton<IEmulatorInfoService, EmulatorInfoService>();
        services.AddSingleton<RuTracker>();
        services.AddSingleton<ThroughputManager>();
        services.AddSingleton<IProgrammabilityEngine, JintProgrammabilityEngine>();
        services.AddSingleton<IQueryEngine, CosmosQueryEngine>();
        services.AddSingleton<IndexValidationService>();
        services.AddSingleton<QueryExplainService>();
        services.AddSingleton<DmlCommandService>();
        services.AddSingleton<Azure.Cosmos.LightEmulator.Triggers.Engine.TriggerEngine>();
        services.AddSingleton<IConsistencyManager>(_ => new ConsistencyManager(ParseConsistencyLevel(emulatorOptions.ConsistencyLevel)));
        services.AddSingleton<IAuthProvider, EmulatorAuthProvider>();
        services.AddSingleton<CosmosResponseHeaderService>();
        services.AddSingleton<KqlSchemaRegistry>(sp =>
        {
            var registry = new KqlSchemaRegistry();
            registry.RegisterTable(new KqlTableSchema("activity",
            [
                new KqlColumnSchema("timestamp", "datetime"),
                new KqlColumnSchema("method", "string"),
                new KqlColumnSchema("path", "string"),
                new KqlColumnSchema("statusCode", "long"),
                new KqlColumnSchema("requestCharge", "real"),
                new KqlColumnSchema("latencyMs", "real"),
                new KqlColumnSchema("databaseId", "string"),
                new KqlColumnSchema("containerId", "string"),
            ]));
            registry.RegisterTable(new KqlTableSchema("telemetry",
            [
                new KqlColumnSchema("timestamp", "datetime"),
                new KqlColumnSchema("databaseId", "string"),
                new KqlColumnSchema("containerId", "string"),
                new KqlColumnSchema("sqlText", "string"),
                new KqlColumnSchema("partitionKey", "string"),
                new KqlColumnSchema("consistencyLevel", "string"),
                new KqlColumnSchema("requestCharge", "real"),
                new KqlColumnSchema("latencyMs", "long"),
                new KqlColumnSchema("itemCount", "long"),
                new KqlColumnSchema("statusCode", "long"),
                new KqlColumnSchema("activityId", "string"),
                new KqlColumnSchema("isCrossPartition", "bool"),
            ]));
            return registry;
        });
        services.AddSingleton<KqlQueryExecutor>();
        services.AddHostedService<TtlCleanupService>();

        services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = null;
                options.JsonSerializerOptions.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
            })
            .AddApplicationPart(typeof(DatabasesController).Assembly)
            .AddApplicationPart(typeof(Azure.Cosmos.LightEmulator.Host.Controllers.QueryTelemetryController).Assembly);

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
    }

    private static void ConfigurePipeline(WebApplication app, EmulatorOptions emulatorOptions)
    {
        // Initialize SurrealDB connection if that storage backend is active
        var surrealManager = app.Services.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>();
        surrealManager?.InitializeAsync().GetAwaiter().GetResult();

        // Wire up activity store into the RU tracker for persistent logging
        var ruTracker = app.Services.GetRequiredService<RuTracker>();
        var activityStore = app.Services.GetRequiredService<IActivityStore>();
        ruTracker.SetActivityStore(activityStore);

        app.UseMiddleware<EmulatorRequestTrackingMiddleware>();
        app.UseMiddleware<CosmosExceptionMiddleware>();
        app.UseMiddleware<CosmosAuthMiddleware>();
        app.UseMiddleware<ConsistencyMiddleware>();

        app.MapMethods("/", ["GET", "HEAD"], async (HttpContext context, CosmosResponseHeaderService responseHeaders, CancellationToken ct) =>
        {
            await responseHeaders.ApplyAsync(context.Response, new CosmosResponseHeaderOptions(), ct);
            return Results.Json(CreateAccountResponse(context));
        });

        app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
        app.MapControllers();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
    }

    private static object CreateAccountResponse(HttpContext context)
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
                defaultConsistencyLevel = "Session"
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

    private static ConsistencyLevel ParseConsistencyLevel(string? value) =>
        Enum.TryParse<ConsistencyLevel>(value, ignoreCase: true, out var consistencyLevel)
            ? consistencyLevel
            : ConsistencyLevel.Session;
}
