using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.InMemory;
using Azure.Cosmos.LightEmulator.Storage.Sqlite;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using Azure.Cosmos.LightEmulator.Storage.Vector;
using Microsoft.Extensions.DependencyInjection;

namespace Azure.Cosmos.LightEmulator.Storage;

/// <summary>
/// Extension methods to register the correct storage backend based on <see cref="StorageType"/>.
/// </summary>
public static class StorageServiceRegistration
{
    /// <summary>
    /// Parses a storage type string into a <see cref="StorageType"/> enum.
    /// Returns <see cref="StorageType.SurrealDb"/> for null/empty/unrecognized values.
    /// </summary>
    public static StorageType ParseStorageType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return StorageType.Sqlite;

        if (Enum.TryParse<StorageType>(value, ignoreCase: true, out var parsed))
            return parsed;

        return StorageType.Sqlite;
    }

    /// <summary>
    /// Registers the appropriate storage services (IDocumentStore, IChangeFeedProvider,
    /// IActivityStore, IQueryTelemetryStore) based on the configured storage type.
    /// </summary>
    public static IServiceCollection AddEmulatorStorage(
        this IServiceCollection services,
        StorageType storageType,
        string dataDirectory,
        VectorIndexOptions? vectorIndexOptions = null)
    {
        var vectorOptions = vectorIndexOptions ?? new VectorIndexOptions();

        switch (storageType)
        {
            case StorageType.InMemory:
                services.AddSingleton<IChangeFeedProvider, InMemoryChangeFeedProvider>();
                services.AddSingleton(sp =>
                    new InMemoryDocumentStore(sp.GetRequiredService<IChangeFeedProvider>()));
                AddVectorLayer<InMemoryDocumentStore>(services, vectorOptions);
                services.AddSingleton<IActivityStore, InMemoryActivityStore>();
                services.AddSingleton<IQueryTelemetryStore, InMemoryQueryTelemetryStore>();
                break;

            case StorageType.Sqlite:
                services.AddSingleton(new SqliteConnectionManager(dataDirectory));
                services.AddSingleton<IChangeFeedProvider>(sp =>
                    new SqliteChangeFeedProvider(sp.GetRequiredService<SqliteConnectionManager>()));
                services.AddSingleton(sp =>
                    new SqliteDocumentStore(
                        sp.GetRequiredService<SqliteConnectionManager>(),
                        sp.GetRequiredService<IChangeFeedProvider>()));
                AddVectorLayer<SqliteDocumentStore>(services, vectorOptions);
                services.AddSingleton<IActivityStore>(sp =>
                    new SqliteActivityStore(sp.GetRequiredService<SqliteConnectionManager>()));
                services.AddSingleton<IQueryTelemetryStore>(sp =>
                    new SqliteQueryTelemetryStore(sp.GetRequiredService<SqliteConnectionManager>()));
                break;

            case StorageType.SurrealDb:
            default:
                services.AddSingleton(sp =>
                {
                    var manager = new SurrealDbConnectionManager(dataDirectory);
                    manager.InitializeAsync().GetAwaiter().GetResult();
                    return manager;
                });
                services.AddSingleton<SurrealDbDocumentStore>();
                AddVectorLayer<SurrealDbDocumentStore>(services, vectorOptions);
                services.AddSingleton<IChangeFeedProvider, SurrealDbChangeFeedProvider>();
                services.AddSingleton<IActivityStore, Azure.Cosmos.LightEmulator.Storage.Telemetry.SurrealDbActivityStore>();
                services.AddSingleton<IQueryTelemetryStore, Azure.Cosmos.LightEmulator.Storage.Telemetry.SurrealDbQueryTelemetryStore>();
                break;
        }

        return services;
    }

    /// <summary>
    /// Registers the vector index provider and the indexing document-store decorator.
    /// The provider depends on the <em>concrete</em> inner store (not <see cref="IDocumentStore"/>)
    /// to avoid a provider → decorator → provider dependency cycle.
    /// </summary>
    private static void AddVectorLayer<TStore>(IServiceCollection services, VectorIndexOptions options)
        where TStore : class, IDocumentStore
    {
        services.AddSingleton<IVectorIndexProvider>(sp =>
            new HnswVectorIndexProvider(sp.GetRequiredService<TStore>(), options));
        services.AddSingleton<IDocumentStore>(sp =>
            new VectorIndexingDocumentStore(
                sp.GetRequiredService<TStore>(),
                sp.GetRequiredService<IVectorIndexProvider>()));
    }
}
