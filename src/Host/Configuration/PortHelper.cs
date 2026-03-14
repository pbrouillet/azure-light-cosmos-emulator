using System.Net;
using System.Net.Sockets;

namespace Azure.Cosmos.LightEmulator.Host.Configuration;

/// <summary>
/// Utility for checking TCP port availability and finding open ports.
/// </summary>
public static class PortHelper
{
    /// <summary>
    /// Tests whether a TCP port is available for binding.
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds an available port starting from <paramref name="preferredPort"/>,
    /// incrementing until an open port is found or <paramref name="maxAttempts"/> is exhausted.
    /// </summary>
    /// <returns>The available port, or -1 if none found within the range.</returns>
    public static int FindAvailablePort(int preferredPort, int maxAttempts = 100)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var candidate = preferredPort + i;
            if (candidate > 65535)
                break;

            if (IsPortAvailable(candidate))
                return candidate;
        }

        return -1;
    }

    /// <summary>
    /// Resolves available ports for the emulator, trying the preferred ports first
    /// and falling back to nearby alternatives. Prints status to the console.
    /// </summary>
    /// <returns>Tuple of (resolvedNoSqlPort, resolvedMongoPort). Throws if no ports are available.</returns>
    public static (int noSqlPort, int mongoPort) ResolveAvailablePorts(int preferredNoSqlPort, int preferredMongoPort)
    {
        var noSqlPort = FindAvailablePort(preferredNoSqlPort);
        if (noSqlPort < 0)
            throw new InvalidOperationException(
                $"Could not find an available port for the NoSQL endpoint starting from {preferredNoSqlPort}.");

        if (noSqlPort != preferredNoSqlPort)
        {
            Console.WriteLine($"  Port {preferredNoSqlPort} is in use. NoSQL endpoint will use port {noSqlPort} instead.");
        }

        // For Mongo port, also make sure it doesn't collide with the resolved NoSQL port
        var mongoPort = FindAvailablePort(preferredMongoPort);
        if (mongoPort < 0)
            throw new InvalidOperationException(
                $"Could not find an available port for the MongoDB endpoint starting from {preferredMongoPort}.");

        if (mongoPort == noSqlPort)
        {
            mongoPort = FindAvailablePort(mongoPort + 1);
            if (mongoPort < 0)
                throw new InvalidOperationException(
                    $"Could not find an available port for the MongoDB endpoint that doesn't collide with NoSQL port {noSqlPort}.");
        }

        if (mongoPort != preferredMongoPort)
        {
            Console.WriteLine($"  Port {preferredMongoPort} is in use. MongoDB endpoint will use port {mongoPort} instead.");
        }

        return (noSqlPort, mongoPort);
    }
}
