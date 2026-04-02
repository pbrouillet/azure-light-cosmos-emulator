using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.InMemory;
using Azure.Cosmos.LightEmulator.Storage.Sqlite;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
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
            return StorageType.SurrealDb;

        if (Enum.TryParse<StorageType>(value, ignoreCase: true, out var parsed))
            return parsed;

        return StorageType.SurrealDb;
    }

    /// <summary>
    /// Registers the appropriate storage services (IDocumentStore, IChangeFeedProvider,
    /// IActivityStore, IQueryTelemetryStore) based on the configured storage type.
    /// </summary>
    public static IServiceCollection AddEmulatorStorage(
        this IServiceCollection services,
        StorageType storageType,
        string dataDirectory)
    {
        switch (storageType)
        {
            case StorageType.InMemory:
                services.AddSingleton<IChangeFeedProvider, InMemoryChangeFeedProvider>();
                services.AddSingleton<IDocumentStore>(sp =>
                    new InMemoryDocumentStore(sp.GetRequiredService<IChangeFeedProvider>()));
                services.AddSingleton<IActivityStore, InMemoryActivityStore>();
                services.AddSingleton<IQueryTelemetryStore, InMemoryQueryTelemetryStore>();
                break;

            case StorageType.Sqlite:
                services.AddSingleton(new SqliteConnectionManager(dataDirectory));
                services.AddSingleton<IChangeFeedProvider>(sp =>
                    new SqliteChangeFeedProvider(sp.GetRequiredService<SqliteConnectionManager>()));
                services.AddSingleton<IDocumentStore>(sp =>
                    new SqliteDocumentStore(
                        sp.GetRequiredService<SqliteConnectionManager>(),
                        sp.GetRequiredService<IChangeFeedProvider>()));
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
                services.AddSingleton<IDocumentStore, SurrealDbDocumentStore>();
                services.AddSingleton<IChangeFeedProvider, SurrealDbChangeFeedProvider>();
                services.AddSingleton<IActivityStore, Azure.Cosmos.LightEmulator.Storage.Telemetry.SurrealDbActivityStore>();
                services.AddSingleton<IQueryTelemetryStore, Azure.Cosmos.LightEmulator.Storage.Telemetry.SurrealDbQueryTelemetryStore>();
                break;
        }

        return services;
    }
}
