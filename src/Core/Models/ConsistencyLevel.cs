namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Cosmos DB consistency levels.
/// </summary>
public enum ConsistencyLevel
{
    Strong,
    BoundedStaleness,
    Session,
    ConsistentPrefix,
    Eventual
}
