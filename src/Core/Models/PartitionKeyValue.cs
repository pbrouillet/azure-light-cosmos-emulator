using System.Text.Json;
using System.Text.Json.Serialization;

namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents the resolved partition key value(s) for a document.
/// </summary>
public class PartitionKeyValue
{
    /// <summary>The component values of the partition key.</summary>
    public required IReadOnlyList<object?> Components { get; init; }

    /// <summary>Creates a single-component partition key.</summary>
    public static PartitionKeyValue Create(object? value) =>
        new() { Components = [value] };

    /// <summary>Creates a multi-component (hierarchical) partition key.</summary>
    public static PartitionKeyValue Create(params object?[] values) =>
        new() { Components = values };

    /// <summary>Represents an undefined partition key.</summary>
    public static PartitionKeyValue Undefined { get; } =
        new() { Components = [] };

    /// <summary>
    /// Serializes to the Cosmos DB partition key header format: ["value"] or ["v1","v2"].
    /// </summary>
    public string ToHeaderString()
    {
        if (Components.Count == 0)
            return "[]";

        var parts = Components.Select(c => c switch
        {
            null => "null",
            string s => JsonSerializer.Serialize(s),
            bool b => b ? "true" : "false",
            _ => c.ToString() ?? "null"
        });

        return $"[{string.Join(",", parts)}]";
    }

    public override bool Equals(object? obj) =>
        obj is PartitionKeyValue other &&
        Components.Count == other.Components.Count &&
        Components.Zip(other.Components).All(pair => Equals(pair.First, pair.Second));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var c in Components)
            hash.Add(c);
        return hash.ToHashCode();
    }
}
