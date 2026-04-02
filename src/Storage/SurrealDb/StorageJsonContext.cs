using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Storage.SurrealDb;

[JsonSerializable(typeof(PartitionKeyDefinition))]
[JsonSerializable(typeof(IndexingPolicy))]
[JsonSerializable(typeof(UniqueKeyPolicy))]
[JsonSerializable(typeof(ConflictResolutionPolicy))]
[JsonSerializable(typeof(VectorEmbeddingPolicy))]
[JsonSerializable(typeof(List<object?>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
