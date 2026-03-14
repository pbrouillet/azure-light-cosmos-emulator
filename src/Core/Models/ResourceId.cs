using System.Security.Cryptography;

namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Generates short resource IDs similar to Cosmos DB _rid values.
/// </summary>
public static class ResourceId
{
    private static long _counter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Generates a short base64-like resource ID.
    /// </summary>
    public static string Generate()
    {
        var value = Interlocked.Increment(ref _counter);
        var bytes = BitConverter.GetBytes(value);
        return Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// Generates ETags for optimistic concurrency.
/// </summary>
public static class ETagGenerator
{
    /// <summary>
    /// Generates a quoted ETag value in the Cosmos DB format.
    /// </summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"\"{hex}\"";
    }
}
