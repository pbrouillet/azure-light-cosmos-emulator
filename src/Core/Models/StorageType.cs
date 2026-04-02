namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Storage backend types supported by the emulator.
/// </summary>
public enum StorageType
{
    /// <summary>SurrealDB over embedded RocksDB (default, persistent).</summary>
    SurrealDb,

    /// <summary>SQLite file-based storage (lightweight, persistent).</summary>
    Sqlite,

    /// <summary>In-memory storage (ephemeral, fastest startup).</summary>
    InMemory
}
