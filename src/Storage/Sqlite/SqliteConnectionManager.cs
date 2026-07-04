using Microsoft.Data.Sqlite;

namespace Azure.Cosmos.LightEmulator.Storage.Sqlite;

/// <summary>
/// Manages SQLite connections and initializes the database schema.
/// </summary>
public class SqliteConnectionManager
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly object _lock = new();

    public SqliteConnectionManager(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "emulator.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public SqliteConnection CreateConnection()
    {
        EnsureInitialized();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // Enable WAL mode for better concurrent read performance
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            InitializeSchema();
            _initialized = true;
        }
    }

    private void InitializeSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS databases (
                id TEXT PRIMARY KEY,
                rid TEXT NOT NULL,
                etag TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                max_throughput INTEGER
            );

            CREATE TABLE IF NOT EXISTS containers (
                id TEXT NOT NULL,
                database_id TEXT NOT NULL,
                rid TEXT NOT NULL,
                etag TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                partition_key_json TEXT NOT NULL,
                indexing_policy_json TEXT,
                default_ttl INTEGER,
                max_throughput INTEGER,
                unique_key_policy_json TEXT,
                conflict_resolution_policy_json TEXT,
                vector_embedding_policy_json TEXT,
                PRIMARY KEY (database_id, id)
            );

            CREATE TABLE IF NOT EXISTS documents (
                id TEXT NOT NULL,
                database_id TEXT NOT NULL,
                container_id TEXT NOT NULL,
                rid TEXT NOT NULL,
                etag TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                partition_key_json TEXT NOT NULL,
                body_json TEXT NOT NULL,
                lsn INTEGER NOT NULL,
                ttl INTEGER,
                is_indexed INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (database_id, container_id, partition_key_json, id)
            );

            CREATE TABLE IF NOT EXISTS users (
                id TEXT NOT NULL,
                database_id TEXT NOT NULL,
                rid TEXT NOT NULL,
                etag TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                PRIMARY KEY (database_id, id)
            );

            CREATE TABLE IF NOT EXISTS permissions (
                id TEXT NOT NULL,
                database_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                rid TEXT NOT NULL,
                etag TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                permission_mode INTEGER NOT NULL,
                resource TEXT NOT NULL,
                token TEXT,
                PRIMARY KEY (database_id, user_id, id)
            );

            CREATE TABLE IF NOT EXISTS offers (
                id TEXT PRIMARY KEY,
                rid TEXT NOT NULL,
                etag TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                offer_throughput INTEGER NOT NULL,
                resource TEXT NOT NULL,
                offer_resource_id TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS changefeed (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                database_id TEXT NOT NULL,
                container_id TEXT NOT NULL,
                document_id TEXT NOT NULL,
                lsn INTEGER NOT NULL,
                change_type INTEGER NOT NULL,
                body_json TEXT NOT NULL,
                previous_image_json TEXT,
                timestamp TEXT NOT NULL,
                partition_key_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_changefeed_container ON changefeed(database_id, container_id, lsn);

            CREATE TABLE IF NOT EXISTS activity (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                method TEXT,
                path TEXT,
                status_code INTEGER,
                request_charge REAL,
                latency_ms REAL,
                database_id TEXT,
                container_id TEXT
            );

            CREATE TABLE IF NOT EXISTS query_telemetry (
                id TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                database_id TEXT,
                container_id TEXT,
                sql_text TEXT,
                partition_key TEXT,
                consistency_level TEXT,
                request_charge REAL,
                latency_ms INTEGER,
                item_count INTEGER,
                status_code INTEGER,
                activity_id TEXT,
                is_cross_partition INTEGER,
                query_plan TEXT
            );
        """;
        cmd.ExecuteNonQuery();
    }
}
