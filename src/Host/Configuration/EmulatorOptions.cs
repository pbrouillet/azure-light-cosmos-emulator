using Azure.Cosmos.LightEmulator.Auth.KeyAuth;

namespace Azure.Cosmos.LightEmulator.Host.Configuration;

/// <summary>
/// Configuration model for the emulator.
/// </summary>
public class EmulatorOptions
{
    public const string SectionName = "Emulator";

    /// <summary>Port for the NoSQL REST API (default: 8081).</summary>
    public int Port { get; set; } = 8081;

    /// <summary>Port for the MongoDB wire protocol (default: 10255).</summary>
    public int MongoPort { get; set; } = 10255;

    /// <summary>Storage backend: SurrealDb (default), Sqlite, or InMemory.</summary>
    public string Storage { get; set; } = "SurrealDb";

    /// <summary>Data directory for persistent storage backends.</summary>
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CosmosEmulator", "data");

    /// <summary>Master key for authentication. Defaults to the well-known Azure Cosmos DB Emulator key.</summary>
    public string MasterKey { get; set; } = Auth.KeyAuth.MasterKeyAuthProvider.DefaultMasterKey;

    /// <summary>Enable EntraID authentication.</summary>
    public bool EnableEntraId { get; set; }

    /// <summary>Azure AD tenant ID (for EntraID auth).</summary>
    public string? TenantId { get; set; }

    /// <summary>Azure AD client/app ID (for EntraID auth).</summary>
    public string? ClientId { get; set; }

    /// <summary>Default consistency level.</summary>
    public string ConsistencyLevel { get; set; } = "Session";

    /// <summary>Enable HTTPS with self-signed certificate.</summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>Enable the explorer UI at /explorer.</summary>
    public bool EnableExplorer { get; set; } = true;

    /// <summary>Verbose logging.</summary>
    public bool Verbose { get; set; }
}
