using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class SpatialFunctionTests
{
    #region ST_DISTANCE

    [Fact]
    public async Task StDistance_PointToPoint_ReturnsMeters()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["location"] = MakePoint(-122.12, 47.67);
            d["target"] = MakePoint(-122.11, 47.67);
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_DISTANCE(c.location, c.target) AS dist FROM c");

        result.Resources.Should().HaveCount(1);
        var dist = result.Resources[0]["dist"]!.GetValue<double>();
        // ~0.01 degree longitude at ~47.67 latitude ≈ ~757m
        dist.Should().BeGreaterThan(500);
        dist.Should().BeLessThan(1500);
    }

    [Fact]
    public async Task StDistance_IdenticalPoints_ReturnsZero()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["location"] = MakePoint(-122.12, 47.67);
            d["target"] = MakePoint(-122.12, 47.67);
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_DISTANCE(c.location, c.target) AS dist FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["dist"]!.GetValue<double>().Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public async Task StDistance_InvalidGeoJson_ReturnsUndefined()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["location"] = "not-a-geojson";
            d["target"] = MakePoint(-122.12, 47.67);
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id, ST_DISTANCE(c.location, c.target) AS dist FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
        result.Resources[0].ContainsKey("dist").Should().BeFalse();
    }

    [Fact]
    public async Task StDistance_WithParameter_Works()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["location"] = MakePoint(-122.12, 47.67);
        }));

        var parameters = new Dictionary<string, object?>
        {
            ["@target"] = MakePoint(-122.11, 47.67)
        };

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_DISTANCE(c.location, @target) AS dist FROM c", parameters);

        result.Resources.Should().HaveCount(1);
        var dist = result.Resources[0]["dist"]!.GetValue<double>();
        dist.Should().BeGreaterThan(500);
        dist.Should().BeLessThan(1500);
    }

    [Fact]
    public async Task StDistance_InWhereClause_FiltersDocuments()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("nearby", "t1", d =>
            {
                d["location"] = MakePoint(-122.12, 47.67);
                d["ref"] = MakePoint(-122.13, 47.67);
            }),
            CreateDocument("far", "t1", d =>
            {
                d["location"] = MakePoint(-73.97, 40.77); // NYC
                d["ref"] = MakePoint(-122.13, 47.67);
            }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id FROM c WHERE ST_DISTANCE(c.location, c.ref) < 10000");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("nearby");
    }

    #endregion

    #region ST_WITHIN

    [Fact]
    public async Task StWithin_PointInsidePolygon_ReturnsTrue()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["location"] = MakePoint(-122.12, 47.67);
            d["boundary"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_WITHIN(c.location, c.boundary) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task StWithin_PointOutsidePolygon_ReturnsFalse()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["location"] = MakePoint(-73.97, 40.77); // NYC
            d["boundary"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_WITHIN(c.location, c.boundary) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task StWithin_InWhereClause_FiltersDocuments()
    {
        var (store, engine) = CreateSut();
        var boundary = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
        await SeedDocumentsAsync(store,
            CreateDocument("inside", "t1", d =>
            {
                d["location"] = MakePoint(-122.12, 47.67);
                d["boundary"] = boundary.DeepClone();
            }),
            CreateDocument("outside", "t1", d =>
            {
                d["location"] = MakePoint(-73.97, 40.77);
                d["boundary"] = boundary.DeepClone();
            }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id FROM c WHERE ST_WITHIN(c.location, c.boundary)");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("inside");
    }

    [Fact]
    public async Task StWithin_WithParameter_Works()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["location"] = MakePoint(-122.12, 47.67);
        }));

        var parameters = new Dictionary<string, object?>
        {
            ["@boundary"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70))
        };

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_WITHIN(c.location, @boundary) FROM c", parameters);

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    #endregion

    #region ST_INTERSECTS

    [Fact]
    public async Task StIntersects_OverlappingPolygons_ReturnsTrue()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["area"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
            d["other"] = MakePolygon((-122.13, 47.66), (-122.08, 47.66), (-122.08, 47.71), (-122.13, 47.71));
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_INTERSECTS(c.area, c.other) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task StIntersects_DisjointPolygons_ReturnsFalse()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["area"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
            d["other"] = MakePolygon((-74.0, 40.7), (-73.9, 40.7), (-73.9, 40.8), (-74.0, 40.8)); // NYC
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_INTERSECTS(c.area, c.other) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task StIntersects_PointInsidePolygon_ReturnsTrue()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["point"] = MakePoint(-122.12, 47.67);
            d["poly"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_INTERSECTS(c.point, c.poly) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    #endregion

    #region ST_ISVALID

    [Fact]
    public async Task StIsValid_ValidPoint_ReturnsTrue()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = MakePoint(-122.12, 47.67);
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_ISVALID(c.geo) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task StIsValid_OutOfRangeLatitude_ReturnsFalse()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = MakePoint(100.0, -200.0); // latitude out of range
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_ISVALID(c.geo) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task StIsValid_ValidPolygon_ReturnsTrue()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_ISVALID(c.geo) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task StIsValid_MissingType_ReturnsFalse()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = new JsonObject
            {
                ["coordinates"] = new JsonArray(-122.12, 47.67)
            };
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_ISVALID(c.geo) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeFalse();
    }

    #endregion

    #region ST_ISVALIDDETAILED

    [Fact]
    public async Task StIsValidDetailed_ValidPoint_ReturnsValidTrue()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = MakePoint(-122.12, 47.67);
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_ISVALIDDETAILED(c.geo) AS detail FROM c");

        result.Resources.Should().HaveCount(1);
        var detail = result.Resources[0]["detail"]!.AsObject();
        detail["valid"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task StIsValidDetailed_InvalidCoordinates_ReturnsReasonString()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = MakePoint(200.0, 100.0); // out of range
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_ISVALIDDETAILED(c.geo) AS detail FROM c");

        result.Resources.Should().HaveCount(1);
        var detail = result.Resources[0]["detail"]!.AsObject();
        detail["valid"]!.GetValue<bool>().Should().BeFalse();
        detail["reason"]!.GetValue<string>().Should().NotBeEmpty();
    }

    #endregion

    #region ST_AREA

    [Fact]
    public async Task StArea_Polygon_ReturnsSquareMeters()
    {
        var (store, engine) = CreateSut();
        // Approx 0.2° x 0.3° rectangle near equator (same as Cosmos DB docs example)
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = MakePolygon((31.8, -5), (32.0, -5), (32.0, -4.7), (31.8, -4.7));
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_AREA(c.geo) AS area FROM c");

        result.Resources.Should().HaveCount(1);
        var area = result.Resources[0]["area"]!.GetValue<double>();
        // Should be approximately 735,970,283 sq meters (from Cosmos DB docs)
        area.Should().BeGreaterThan(500_000_000);
        area.Should().BeLessThan(1_000_000_000);
    }

    [Fact]
    public async Task StArea_Point_ReturnsZero()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = MakePoint(-122.12, 47.67);
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_AREA(c.geo) AS area FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["area"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task StArea_LineString_ReturnsZero()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["geo"] = new JsonObject
            {
                ["type"] = "LineString",
                ["coordinates"] = new JsonArray(
                    new JsonArray(-122.12, 47.67),
                    new JsonArray(-122.13, 47.68))
            };
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT ST_AREA(c.geo) AS area FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["area"]!.GetValue<double>().Should().Be(0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task StDistance_MissingField_ReturnsUndefined()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["target"] = MakePoint(0, 0);
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id, ST_DISTANCE(c.nonexistent, c.target) AS dist FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0].ContainsKey("dist").Should().BeFalse();
    }

    [Fact]
    public async Task StWithin_LineStringInPolygon()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
        {
            d["route"] = new JsonObject
            {
                ["type"] = "LineString",
                ["coordinates"] = new JsonArray(
                    new JsonArray(-122.12, 47.67),
                    new JsonArray(-122.13, 47.68))
            };
            d["boundary"] = MakePolygon((-122.15, 47.65), (-122.10, 47.65), (-122.10, 47.70), (-122.15, 47.70));
        }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE ST_WITHIN(c.route, c.boundary) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<bool>().Should().BeTrue();
    }

    #endregion

    #region Helpers

    private static JsonObject MakePoint(double lon, double lat)
    {
        return new JsonObject
        {
            ["type"] = "Point",
            ["coordinates"] = new JsonArray(lon, lat)
        };
    }

    private static JsonObject MakePolygon(params (double lon, double lat)[] vertices)
    {
        var ring = new JsonArray();
        foreach (var (lon, lat) in vertices)
            ring.Add(new JsonArray(lon, lat));
        // Close the ring
        ring.Add(new JsonArray(vertices[0].lon, vertices[0].lat));

        return new JsonObject
        {
            ["type"] = "Polygon",
            ["coordinates"] = new JsonArray(ring)
        };
    }

    private static async Task SeedDocumentsAsync(IDocumentStore store, params JsonObject[] documents)
    {
        foreach (var document in documents)
        {
            await store.CreateDocumentAsync("db", "coll", document);
        }
    }

    private static JsonObject CreateDocument(string id, string tenantId, Action<JsonObject>? configure = null)
    {
        var document = new JsonObject
        {
            ["id"] = id,
            ["tenantId"] = tenantId
        };

        configure?.Invoke(document);
        return document;
    }

    private static (IDocumentStore Store, CosmosQueryEngine Engine) CreateSut()
    {
        var store = new SurrealDbDocumentStore(
            new SurrealDbConnectionManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            new InMemoryChangeFeedProvider());

        store.CreateDatabaseAsync("db").GetAwaiter().GetResult();
        store.CreateContainerAsync("db", new CosmosContainer
        {
            Id = "coll",
            DatabaseId = "db",
            PartitionKey = new PartitionKeyDefinition { Paths = ["/tenantId"] },
            IndexingPolicy = new IndexingPolicy
            {
                SpatialIndexes =
                [
                    new SpatialIndex
                    {
                        Path = "/location/*",
                        Types = [SpatialType.Point, SpatialType.Polygon]
                    }
                ]
            }
        }).GetAwaiter().GetResult();

        return (store, new CosmosQueryEngine(store, new IndexValidationService()));
    }

    #endregion
}
