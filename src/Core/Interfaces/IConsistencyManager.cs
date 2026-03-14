using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Manages consistency levels and session tokens.
/// </summary>
public interface IConsistencyManager
{
    /// <summary>Gets the default consistency level for the account.</summary>
    ConsistencyLevel DefaultConsistencyLevel { get; }

    /// <summary>
    /// Validates that the requested consistency level is valid.
    /// </summary>
    bool IsValidConsistencyLevel(ConsistencyLevel requested);

    /// <summary>
    /// Gets the effective consistency level for a request.
    /// </summary>
    ConsistencyLevel GetEffectiveConsistency(ConsistencyLevel? requested);

    /// <summary>
    /// Generates a new session token for a write operation.
    /// </summary>
    string GenerateSessionToken(string databaseId, string containerId, long lsn);

    /// <summary>
    /// Validates a session token for read consistency.
    /// </summary>
    bool ValidateSessionToken(string databaseId, string containerId, string? sessionToken);

    /// <summary>
    /// Gets the current session token for a container.
    /// </summary>
    string GetCurrentSessionToken(string databaseId, string containerId);
}
