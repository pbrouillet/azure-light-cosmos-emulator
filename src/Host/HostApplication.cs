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


    public static WebApplicationBuilder CreateBuilder(string[]? args = null) =>
        WebApplication.CreateBuilder(args ?? []);

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var emulatorOptions = builder.Configuration.GetSection(EmulatorOptions.SectionName).Get<EmulatorOptions>() ?? new EmulatorOptions();

        Directory.CreateDirectory(emulatorOptions.DataDirectory);
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
        services.AddSingleton<EmulatorAdminSettingsStore>(sp =>
            new EmulatorAdminSettingsStore(
                sp.GetRequiredService<IOptions<EmulatorOptions>>(),
                sp.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>()));
        services.AddSingleton<IEmulatorInfoService, EmulatorInfoService>();
        services.AddSingleton<RuTracker>();
        services.AddSingleton<ThroughputManager>();
        services.AddSingleton<Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.IProgrammabilityRecordStore>(sp =>
        {
            var surrealManager = sp.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>();
            if (surrealManager is not null)
                return new Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.SurrealDbProgrammabilityRecordStore(surrealManager);
            return new Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.InMemoryProgrammabilityRecordStore();
        });
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
        services.AddHostedService<DataMaintenanceService>();

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
            return Results.Json(AccountMetadataHelper.CreateAccountResponse(context, emulatorOptions.ConsistencyLevel));
        });

        app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
        app.MapControllers();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
    }

    private static ConsistencyLevel ParseConsistencyLevel(string? value) =>
        Enum.TryParse<ConsistencyLevel>(value, ignoreCase: true, out var consistencyLevel)
            ? consistencyLevel
            : ConsistencyLevel.Session;
}
