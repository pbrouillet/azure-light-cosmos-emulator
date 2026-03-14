using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Middleware;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
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
        emulatorOptions.MasterKey ??= MasterKeyAuthProvider.GenerateMasterKey();

        ConfigureServices(builder.Services, emulatorOptions);

        var app = builder.Build();
        ConfigurePipeline(app, emulatorOptions);
        return app;
    }

    private static void ConfigureServices(IServiceCollection services, EmulatorOptions emulatorOptions)
    {
        services.AddSingleton(emulatorOptions);
        services.AddSingleton<IOptions<EmulatorOptions>>(_ => Options.Create(emulatorOptions));
        services.AddSingleton(new SurrealDbConnectionManager(emulatorOptions.DataDirectory));
        services.AddSingleton<IChangeFeedProvider, InMemoryChangeFeedProvider>();
        services.AddSingleton<IDocumentStore, SurrealDbDocumentStore>();
        services.AddSingleton<EmulatorAdminSettingsStore>();
        services.AddSingleton<IEmulatorInfoService, EmulatorInfoService>();
        services.AddSingleton<RuTracker>();
        services.AddSingleton<IProgrammabilityEngine, JintProgrammabilityEngine>();
        services.AddSingleton<IQueryEngine, CosmosQueryEngine>();
        services.AddSingleton<IAuthProvider, EmulatorAuthProvider>();

        services
            .AddControllers()
            .AddApplicationPart(typeof(DatabasesController).Assembly);

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
    }

    private static void ConfigurePipeline(WebApplication app, EmulatorOptions emulatorOptions)
    {
        app.Services.GetRequiredService<SurrealDbConnectionManager>().InitializeAsync().GetAwaiter().GetResult();

        app.UseMiddleware<EmulatorRequestTrackingMiddleware>();
        app.UseMiddleware<CosmosExceptionMiddleware>();
        app.UseMiddleware<CosmosAuthMiddleware>();

        app.MapMethods("/", ["GET", "HEAD"], (HttpContext context) =>
        {
            SetCommonHeaders(context.Response);
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

    private static void SetCommonHeaders(HttpResponse response)
    {
        response.Headers[CosmosHeaders.RequestCharge] = "1";
        response.Headers[CosmosHeaders.ActivityId] = Guid.NewGuid().ToString();
        response.Headers[CosmosHeaders.ServiceVersion] = CosmosHeaders.CurrentServiceVersion;
        response.Headers[CosmosHeaders.SchemaVersion] = CosmosHeaders.CurrentSchemaVersion;
    }
}
