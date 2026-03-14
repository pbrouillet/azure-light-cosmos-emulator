using SurrealDb.Net;
using SurrealDb.Net.Internals;
using System.Reflection;

var assembly = typeof(SurrealDbClient).Assembly;

Console.WriteLine("=== InMemory and RocksDb Engine Implementations ===");
var types = assembly.GetTypes();

// Find implementations of the interfaces
var inMemoryInterface = types.FirstOrDefault(t => t.Name == "ISurrealDbInMemoryEngine");
var rocksDbInterface = types.FirstOrDefault(t => t.Name == "ISurrealDbRocksDbEngine");

Console.WriteLine($"ISurrealDbInMemoryEngine interface found: {inMemoryInterface != null}");
Console.WriteLine($"ISurrealDbRocksDbEngine interface found: {rocksDbInterface != null}");

// Check if there are concrete implementations
var implementations = types.Where(t => 
    (inMemoryInterface != null && inMemoryInterface.IsAssignableFrom(t) && t.IsClass) ||
    (rocksDbInterface != null && rocksDbInterface.IsAssignableFrom(t) && t.IsClass)
).ToList();

Console.WriteLine($"Implementations found: {implementations.Count}");
foreach (var impl in implementations)
{
    Console.WriteLine($"  - {impl.FullName}");
}

Console.WriteLine("");
Console.WriteLine("=== Checking for provider-based engine setup ===");
var providerEngine = types.FirstOrDefault(t => t.Name == "ISurrealDbProviderEngine");
if (providerEngine != null)
{
    Console.WriteLine($"ISurrealDbProviderEngine found - indicates provider-based architecture");
    var providerImplementations = types.Where(t => providerEngine.IsAssignableFrom(t) && t.IsClass).ToList();
    foreach (var prov in providerImplementations)
    {
        Console.WriteLine($"  - {prov.FullName}");
    }
}

Console.WriteLine("");
Console.WriteLine("=== Dependency Injection Extension Methods ===");
var serviceCollectionExt = types.FirstOrDefault(t => t.Name == "ServiceCollectionExtensions");
if (serviceCollectionExt != null)
{
    var methods = serviceCollectionExt.GetMethods(BindingFlags.Public | BindingFlags.Static);
    foreach (var method in methods)
    {
        Console.WriteLine($"  - {method.Name}");
    }
}
