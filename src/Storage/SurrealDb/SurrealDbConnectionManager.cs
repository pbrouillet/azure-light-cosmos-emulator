using SurrealDb.Embedded.RocksDb;
using SurrealDb.Net;

namespace Azure.Cosmos.LightEmulator.Storage.SurrealDb;

/// <summary>
/// Manages the SurrealDB embedded connection with RocksDB backend.
/// </summary>
public class SurrealDbConnectionManager : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private SurrealDbRocksDbClient? _client;

    public SurrealDbConnectionManager(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    public ISurrealDbClient Client => _client ?? throw new InvalidOperationException("Not initialized");

    /// <summary>
    /// Gets the connection string for the embedded SurrealDB instance.
    /// </summary>
    public string ConnectionString => $"rocksdb://{Path.Combine(_dataDirectory, "surreal.db")}";

    /// <summary>
    /// Initializes the SurrealDB embedded instance.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            return;
        }

        await _initializationLock.WaitAsync(ct);
        try
        {
            if (_client is not null)
            {
                return;
            }

            _client = await CreateClientAsync(ct);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <summary>
    /// Resets all data by clearing the data directory.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _initializationLock.WaitAsync(ct);
        try
        {
            _client?.Dispose();
            _client = null;

            await WaitForUnlockedFilesAsync(ct);
            await DeleteDataDirectoryAsync(ct);

            Directory.CreateDirectory(_dataDirectory);
            _client = await CreateClientAsync(ct);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _initializationLock.WaitAsync();
        try
        {
            _client?.Dispose();
            _client = null;
            await WaitForUnlockedFilesAsync(CancellationToken.None);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SurrealDbRocksDbClient> CreateClientAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_dataDirectory);

        var dbPath = Path.Combine(_dataDirectory, "surreal.db");
        var client = new SurrealDbRocksDbClient(dbPath);
        await client.Connect(ct);
        await client.Use("emulator", "cosmos", ct);
        return client;
    }

    private async Task DeleteDataDirectoryAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_dataDirectory))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
        }
    }

    private async Task WaitForUnlockedFilesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_dataDirectory))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (AreFilesUnlocked(_dataDirectory))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
        }
    }

    private static bool AreFilesUnlocked(string directory)
    {
        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }
}
