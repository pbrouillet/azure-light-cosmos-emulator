namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Estimates Request Unit (RU) costs for Cosmos DB operations,
/// mimicking the real service's pricing model.
/// </summary>
public static class RuCostCalculator
{
    /// <summary>Point read: ~1 RU per 1KB.</summary>
    public static double PointRead(int documentSizeBytes = 1024) =>
        Math.Max(1.0, Math.Ceiling(documentSizeBytes / 1024.0));

    /// <summary>Create: base 5 RU + ~1 RU per KB.</summary>
    public static double Create(int documentSizeBytes) =>
        5.0 + Math.Ceiling(documentSizeBytes / 1024.0);

    /// <summary>Replace: base 5 RU + ~1 RU per KB.</summary>
    public static double Replace(int documentSizeBytes) =>
        5.0 + Math.Ceiling(documentSizeBytes / 1024.0);

    /// <summary>Upsert: base 5 RU + ~1 RU per KB.</summary>
    public static double Upsert(int documentSizeBytes) =>
        5.0 + Math.Ceiling(documentSizeBytes / 1024.0);

    /// <summary>Delete: flat 5 RU.</summary>
    public static double Delete() => 5.0;

    /// <summary>
    /// Query cost: base 2.5 RU + result cost.
    /// Cross-partition queries pay a multiplier per partition scanned.
    /// </summary>
    public static double Query(int resultCount, int totalResultSizeBytes, bool isCrossPartition, int partitionCount = 1, double scanMultiplier = 1.0)
    {
        var baseCost = 2.5;
        var resultCost = resultCount * 0.5 + Math.Ceiling(Math.Max(1, totalResultSizeBytes) / 1024.0);
        var multiplier = isCrossPartition ? Math.Max(2, partitionCount) : 1;
        return Math.Round((baseCost + resultCost) * multiplier * scanMultiplier, 2);
    }

    public static double ListDatabases() => 1.0;
    public static double ListContainers() => 1.0;
    public static double GetDatabase() => 1.0;
    public static double GetContainer() => 1.0;
    public static double CreateDatabase() => 5.0;
    public static double DeleteDatabase() => 5.0;
    public static double CreateContainer() => 5.0;
    public static double DeleteContainer() => 5.0;
    public static double CreateProgrammability() => 5.0;
    public static double ListProgrammability() => 1.0;
    public static double ExecuteProgrammability() => 5.0;
    public static double DeleteProgrammability() => 5.0;
}
