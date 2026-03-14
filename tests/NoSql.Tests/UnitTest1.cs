using System.Dynamic;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class UnitTest1
{
    [Fact]
    public async Task ExecuteStoredProcedureAsync_UsesCosmosContextAndReturnsResponseBody()
    {
        var (store, engine) = CreateSut();

        await engine.CreateStoredProcedureAsync("db", "coll", new StoredProcedure
        {
            Id = "create-doc",
            DatabaseId = "db",
            ContainerId = "coll",
            Body = """
                function(prefix) {
                    var context = getContext();
                    var collection = context.getCollection();
                    var response = context.getResponse();
                    var createdId = null;

                    var accepted = collection.createDocument(
                        collection.getSelfLink(),
                        { id: prefix + '-1', tenantId: 'tenant-1', name: prefix },
                        {},
                        function(err, doc) {
                            if (err) {
                                throw new Error(err.message);
                            }
                            createdId = doc.id;
                        });

                    response.setBody({ accepted: accepted, id: createdId, selfLink: collection.getSelfLink() });
                }
                """
        });

        var result = await engine.ExecuteStoredProcedureAsync("db", "coll", "create-doc", new object?[] { "item" }, PartitionKeyValue.Create("tenant-1"));
        var response = AsDictionary(result);

        response["accepted"].Should().Be(true);
        response["id"].Should().Be("item-1");
        response["selfLink"].Should().Be("dbs/db/colls/coll/");

        var stored = await store.ReadDocumentAsync("db", "coll", "item-1", PartitionKeyValue.Create("tenant-1"));
        stored.ToResponseBody()["name"]!.GetValue<string>().Should().Be("item");
    }

    [Fact]
    public async Task ExecuteStoredProcedureAsync_SupportsCrudCallbacksAndQueryDocuments()
    {
        var (store, engine) = CreateSut();
        await store.CreateDocumentAsync("db", "coll", new JsonObject
        {
            ["id"] = "doc-1",
            ["tenantId"] = "tenant-1",
            ["name"] = "original"
        });
        await store.CreateDocumentAsync("db", "coll", new JsonObject
        {
            ["id"] = "doc-2",
            ["tenantId"] = "tenant-1",
            ["name"] = "to-delete"
        });

        await engine.CreateStoredProcedureAsync("db", "coll", new StoredProcedure
        {
            Id = "crud-docs",
            DatabaseId = "db",
            ContainerId = "coll",
            Body = """
                function() {
                    var context = getContext();
                    var collection = context.getCollection();
                    var response = context.getResponse();
                    var doc1Link = 'dbs/db/colls/coll/docs/doc-1/';
                    var doc2Link = 'dbs/db/colls/coll/docs/doc-2/';

                    collection.readDocument(doc1Link, {}, function(err, doc) {
                        if (err) {
                            throw new Error(err.message);
                        }

                        doc.name = 'updated';
                        collection.replaceDocument(doc1Link, doc, {}, function(replaceErr, replaced) {
                            if (replaceErr) {
                                throw new Error(replaceErr.message);
                            }

                            collection.deleteDocument(doc2Link, {}, function(deleteErr) {
                                if (deleteErr) {
                                    throw new Error(deleteErr.message);
                                }

                                collection.queryDocuments(collection.getSelfLink(), 'SELECT * FROM c', {}, function(queryErr, docs) {
                                    if (queryErr) {
                                        throw new Error(queryErr.message);
                                    }

                                    response.setBody({ name: replaced.name, count: docs.length });
                                });
                            });
                        });
                    });
                }
                """
        });

        var result = await engine.ExecuteStoredProcedureAsync("db", "coll", "crud-docs", Array.Empty<object?>(), PartitionKeyValue.Create("tenant-1"));
        var response = AsDictionary(result);

        response["name"].Should().Be("updated");
        Convert.ToInt32(response["count"]).Should().Be(1);

        var updated = await store.ReadDocumentAsync("db", "coll", "doc-1", PartitionKeyValue.Create("tenant-1"));
        updated.ToResponseBody()["name"]!.GetValue<string>().Should().Be("updated");

        var delete = () => store.ReadDocumentAsync("db", "coll", "doc-2", PartitionKeyValue.Create("tenant-1"));
        await delete.Should().ThrowAsync<CosmosEmulatorException>();
    }

    [Fact]
    public async Task ExecuteStoredProcedureAsync_WrapsJavaScriptErrors()
    {
        var (_, engine) = CreateSut();

        await engine.CreateStoredProcedureAsync("db", "coll", new StoredProcedure
        {
            Id = "throwing-sproc",
            DatabaseId = "db",
            ContainerId = "coll",
            Body = "function() { throw new Error('boom'); }"
        });

        var act = () => engine.ExecuteStoredProcedureAsync("db", "coll", "throwing-sproc", Array.Empty<object?>(), PartitionKeyValue.Create("tenant-1"));

        var exception = await act.Should().ThrowAsync<CosmosEmulatorException>();
        exception.Which.ErrorCode.Should().Be("BadRequest");
        exception.Which.Message.Should().Contain("boom");
    }

    private static (IDocumentStore Store, JintProgrammabilityEngine Engine) CreateSut()
    {
        var connectionManager = new SurrealDbConnectionManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var store = new SurrealDbDocumentStore(
            connectionManager,
            new InMemoryChangeFeedProvider());

        store.CreateDatabaseAsync("db").GetAwaiter().GetResult();
        store.CreateContainerAsync("db", new CosmosContainer
        {
            Id = "coll",
            DatabaseId = "db",
            PartitionKey = new PartitionKeyDefinition
            {
                Paths = ["/tenantId"]
            }
        }).GetAwaiter().GetResult();

        return (store, new JintProgrammabilityEngine(store, connectionManager));
    }

    private static IDictionary<string, object?> AsDictionary(object? value)
    {
        value.Should().BeAssignableTo<ExpandoObject>();
        return (IDictionary<string, object?>)value!;
    }
}
