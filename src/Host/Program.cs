using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Consistency;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Azure.Cosmos.LightEmulator.NoSql.Middleware;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Azure.Cosmos.LightEmulator.Host;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var app = BuildApplication(args);
        await app.RunAsync();
    }

    public static WebApplication BuildApplication(
        string[] args,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args
        });

        // Load optional side config file from the executable directory.
        // Priority: appsettings.json < emulator-config.json < CLI overrides
        var configFilePath = Path.Combine(AppContext.BaseDirectory, "emulator-config.json");
        builder.Configuration.AddJsonFile(configFilePath, optional: true, reloadOnChange: true);

        if (configurationOverrides is { Count: > 0 })
        {
            builder.Configuration.AddInMemoryCollection(configurationOverrides);
        }

        var emulatorOptions = BindOptions(builder.Configuration);

        Directory.CreateDirectory(emulatorOptions.DataDirectory);

        // Resolve available ports before binding
        var (resolvedPort, resolvedMongoPort) = PortHelper.ResolveAvailablePorts(
            emulatorOptions.Port, emulatorOptions.MongoPort);
        emulatorOptions.Port = resolvedPort;
        emulatorOptions.MongoPort = resolvedMongoPort;

        // Push resolved ports back into configuration so downstream services see the actual values
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{EmulatorOptions.SectionName}:{nameof(EmulatorOptions.Port)}"] = resolvedPort.ToString(),
            [$"{EmulatorOptions.SectionName}:{nameof(EmulatorOptions.MongoPort)}"] = resolvedMongoPort.ToString()
        });

        builder.Host.UseSerilog((context, services, loggerConfiguration) =>
        {
            var configuredOptions = BindOptions(context.Configuration);
            loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .MinimumLevel.Is(configuredOptions.Verbose ? LogEventLevel.Debug : LogEventLevel.Information)
                .WriteTo.Console();
        });

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenAnyIP(resolvedPort, listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
                if (emulatorOptions.EnableSsl)
                {
                    listenOptions.UseHttps();
                }
            });
        });

        builder.Services.Configure<EmulatorOptions>(builder.Configuration.GetSection(EmulatorOptions.SectionName));
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                // Cosmos DB REST API uses PascalCase for collection properties
                // (Databases, DocumentCollections, Documents, etc.)
                options.JsonSerializerOptions.PropertyNamingPolicy = null;
                options.JsonSerializerOptions.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
            })
            .AddApplicationPart(typeof(DatabasesController).Assembly)
            .AddApplicationPart(typeof(Azure.Cosmos.LightEmulator.Host.Controllers.QueryTelemetryController).Assembly);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        });

        var storageType = StorageServiceRegistration.ParseStorageType(emulatorOptions.Storage);
        builder.Services.AddEmulatorStorage(storageType, emulatorOptions.DataDirectory);
        builder.Services.AddSingleton<EmulatorRuntimeState>();
        builder.Services.AddSingleton<EmulatorAdminSettingsStore>(sp =>
            new EmulatorAdminSettingsStore(
                sp.GetRequiredService<IOptions<EmulatorOptions>>(),
                sp.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>()));
        builder.Services.AddSingleton<IEmulatorInfoService, EmulatorInfoService>();
        builder.Services.AddSingleton<RuTracker>();
        builder.Services.AddSingleton<ThroughputManager>();
        builder.Services.AddSingleton<IQueryEngine, CosmosQueryEngine>();
        builder.Services.AddSingleton<QueryExplainService>();
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.NoSql.Query.IQueryTelemetryRecorder, Azure.Cosmos.LightEmulator.NoSql.Query.QueryTelemetryRecorder>();
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.NoSql.Query.IQueryExecutionLimiter>(
            _ => new Azure.Cosmos.LightEmulator.NoSql.Query.QueryExecutionLimiter(emulatorOptions.MaxConcurrentQueries));
        builder.Services.AddSingleton<IndexValidationService>();
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.NoSql.Query.DmlCommandService>();
        builder.Services.AddSingleton<IConsistencyManager>(_ => new ConsistencyManager(ParseConsistencyLevel(emulatorOptions.ConsistencyLevel)));
        builder.Services.AddSingleton<IAuthProvider, EmulatorAuthProvider>();
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.IProgrammabilityRecordStore>(sp =>
        {
            var surrealManager = sp.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>();
            if (surrealManager is not null)
                return new Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.SurrealDbProgrammabilityRecordStore(surrealManager);
            return new Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.InMemoryProgrammabilityRecordStore();
        });
        builder.Services.AddSingleton<IProgrammabilityEngine, Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.JintProgrammabilityEngine>();
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.Triggers.Engine.TriggerEngine>();
        builder.Services.AddSingleton<CosmosResponseHeaderService>();
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.Kql.KqlSchemaRegistry>(sp =>
        {
            var registry = new Azure.Cosmos.LightEmulator.Kql.KqlSchemaRegistry();
            registry.RegisterTable(new Azure.Cosmos.LightEmulator.Kql.KqlTableSchema("activity",
            [
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("timestamp", "datetime"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("method", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("path", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("statusCode", "long"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("requestCharge", "real"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("latencyMs", "real"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("databaseId", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("containerId", "string"),
            ]));
            registry.RegisterTable(new Azure.Cosmos.LightEmulator.Kql.KqlTableSchema("telemetry",
            [
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("timestamp", "datetime"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("databaseId", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("containerId", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("sqlText", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("partitionKey", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("consistencyLevel", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("requestCharge", "real"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("latencyMs", "long"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("itemCount", "long"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("statusCode", "long"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("activityId", "string"),
                new Azure.Cosmos.LightEmulator.Kql.KqlColumnSchema("isCrossPartition", "bool"),
            ]));
            return registry;
        });
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.Kql.KqlQueryExecutor>();
        builder.Services.AddHostedService<TtlCleanupService>();
        builder.Services.AddHostedService<DataMaintenanceService>();

        var app = builder.Build();
        var logger = app.Logger;

        app.UseSerilogRequestLogging();
        app.UseMiddleware<EmulatorRequestTrackingMiddleware>();
        app.UseMiddleware<CosmosExceptionMiddleware>();
        app.UseMiddleware<ThroughputEnforcementMiddleware>();
        app.UseMiddleware<CosmosAuthMiddleware>();
        app.UseMiddleware<ConsistencyMiddleware>();

        if (emulatorOptions.EnableExplorer)
        {
            var explorerProvider = ResolveExplorerFileProvider(app.Environment.ContentRootPath);
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                RequestPath = "/explorer",
                FileProvider = explorerProvider
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = "/explorer",
                FileProvider = explorerProvider
            });
            app.MapFallback("/explorer/{*path:nonfile}", async context =>
            {
                var indexFile = explorerProvider.GetFileInfo("index.html");
                if (indexFile.Exists)
                {
                    context.Response.ContentType = "text/html";
                    await using var stream = indexFile.CreateReadStream();
                    await stream.CopyToAsync(context.Response.Body);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
            });
        }

        app.MapMethods("/", ["GET", "HEAD"], async (HttpContext context, CosmosResponseHeaderService responseHeaders, CancellationToken ct) =>
        {
            await responseHeaders.ApplyAsync(context.Response, new CosmosResponseHeaderOptions(), ct);
            return Results.Json(AccountMetadataHelper.CreateAccountResponse(context, emulatorOptions.ConsistencyLevel));
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapControllers();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var scheme = emulatorOptions.EnableSsl ? "https" : "http";
            var noSqlEndpoint = $"{scheme}://localhost:{emulatorOptions.Port}";
            var mongoEndpoint = $"mongodb://localhost:{emulatorOptions.MongoPort}";
            var connectionString = $"AccountEndpoint={noSqlEndpoint};AccountKey={emulatorOptions.MasterKey};";

            const int width = 110;
            const int inner = width - 2;
            var border = new string('═', inner);

            Console.WriteLine();
            Console.WriteLine($"╔{border}╗");
            Console.WriteLine($"║{"  Azure Cosmos DB Light Emulator",-108}║");
            Console.WriteLine($"╠{border}╣");
            Console.WriteLine($"║  NoSQL Endpoint:   {noSqlEndpoint,-88}║");
            Console.WriteLine($"║  MongoDB Endpoint: {mongoEndpoint,-88}║");
            if (emulatorOptions.EnableExplorer)
                Console.WriteLine($"║  Explorer:         {noSqlEndpoint + "/explorer",-88}║");
            Console.WriteLine($"║  Consistency:      {emulatorOptions.ConsistencyLevel,-88}║");
            Console.WriteLine($"║  Storage:          {storageType,-88}║");
            Console.WriteLine($"╠{border}╣");
            Console.WriteLine($"║{"  Master Key:",-108}║");
            Console.WriteLine($"║    {emulatorOptions.MasterKey,-104}║");
            Console.WriteLine($"╠{border}╣");
            Console.WriteLine($"║{"  Connection String:",-108}║");
            // Break long connection string across lines
            for (var i = 0; i < connectionString.Length; i += 104)
            {
                var chunk = connectionString.Substring(i, Math.Min(104, connectionString.Length - i));
                Console.WriteLine($"║    {chunk,-104}║");
            }
            Console.WriteLine($"╚{border}╝");
            Console.WriteLine();

            logger.LogInformation("Azure Cosmos Light Emulator listening on {Endpoint}", noSqlEndpoint);
        });

        return app;
    }

    private static EmulatorOptions BindOptions(IConfiguration configuration)
    {
        var options = configuration.GetSection(EmulatorOptions.SectionName).Get<EmulatorOptions>() ?? new EmulatorOptions();

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            options.DataDirectory = new EmulatorOptions().DataDirectory;
        }

        return options;
    }

    private static ConsistencyLevel ParseConsistencyLevel(string? value) =>
        Enum.TryParse<ConsistencyLevel>(value, ignoreCase: true, out var consistencyLevel)
            ? consistencyLevel
            : ConsistencyLevel.Session;

    /// <summary>
    /// Returns an <see cref="IFileProvider"/> for the Explorer SPA assets.
    /// In Release/single-file builds the assets are embedded in the assembly via
    /// <see cref="ManifestEmbeddedFileProvider"/>. In Debug/development builds the
    /// assets are served from the physical <c>wwwroot/explorer</c> directory.
    /// </summary>
    private static IFileProvider ResolveExplorerFileProvider(string contentRoot)
    {
        // Try embedded resources first (Release / single-file publish)
        try
        {
            var embedded = new ManifestEmbeddedFileProvider(
                typeof(Program).Assembly, "wwwroot/explorer");
            if (embedded.GetFileInfo("index.html").Exists)
                return embedded;
        }
        catch (InvalidOperationException)
        {
            // No embedded manifest — Debug build or assets not embedded
        }

        // Fall back to physical file system
        var explorerRoot = ResolveExplorerRoot(contentRoot);
        Directory.CreateDirectory(explorerRoot);
        return new PhysicalFileProvider(explorerRoot);
    }

    /// <summary>
    /// Finds the explorer wwwroot directory by checking multiple candidate paths.
    /// Supports: dotnet run (content root = project dir), CLI launch (assembly dir),
    /// and published/Docker scenarios.
    /// </summary>
    private static string ResolveExplorerRoot(string contentRoot)
    {
        var hostAssemblyDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(contentRoot, "wwwroot", "explorer"),
            Path.Combine(hostAssemblyDir, "wwwroot", "explorer"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "explorer"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;
        }

        // Return the content-root-based path as the default (it will be created empty)
        return candidates[0];
    }
}
