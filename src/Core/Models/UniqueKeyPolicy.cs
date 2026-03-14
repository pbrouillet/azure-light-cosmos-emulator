namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Unique key policy for a container.
/// </summary>
public class UniqueKeyPolicy
{
    public List<UniqueKey> UniqueKeys { get; set; } = [];
}

public class UniqueKey
{
    public required List<string> Paths { get; set; }
}
