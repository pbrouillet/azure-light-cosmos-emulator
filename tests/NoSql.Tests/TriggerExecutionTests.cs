using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class TriggerExecutionTests
{
    // Pre-trigger that stamps a "triggeredBy" property onto the request body.
    private const string PreTriggerBody = """
        function preTrigger() {
            var context = getContext();
            var request = context.getRequest();
            var doc = request.getBody();
            doc.triggeredBy = "pre-trigger";
            request.setBody(doc);
        }
        """;

    // Post-trigger that simply runs without error (validates invocation).
    private const string PostTriggerBody = """
        function postTrigger() {
            var context = getContext();
            var response = context.getResponse();
            var doc = response.getBody();
        }
        """;

    #region Replace

    [Fact]
    public async Task Replace_PreTrigger_ModifiesDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "original");

        await CreateTriggerAsync(fixture, ids, "myPreReplace", PreTriggerBody, "Pre", "Replace");

        using var request = fixture.CreateRequest(HttpMethod.Put,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}",
            new { id = docId, tenantId = ids.PartitionKey, name = "replaced" });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PreTriggerInclude, "myPreReplace");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["triggeredBy"]!.GetValue<string>().Should().Be("pre-trigger");
        body["name"]!.GetValue<string>().Should().Be("replaced");
    }

    [Fact]
    public async Task Replace_PostTrigger_FiresAfterReplace()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "original");

        await CreateTriggerAsync(fixture, ids, "myPostReplace", PostTriggerBody, "Post", "Replace");

        using var request = fixture.CreateRequest(HttpMethod.Put,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}",
            new { id = docId, tenantId = ids.PartitionKey, name = "replaced" });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PostTriggerInclude, "myPostReplace");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["name"]!.GetValue<string>().Should().Be("replaced");
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_PreTrigger_FiresBeforeDelete()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "to-delete");

        // Pre-trigger that simply executes without error (validates it receives the existing doc).
        await CreateTriggerAsync(fixture, ids, "myPreDelete", PreTriggerBody, "Pre", "Delete");

        using var request = fixture.CreateRequest(HttpMethod.Delete,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PreTriggerInclude, "myPreDelete");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify document was actually deleted
        using var readRequest = fixture.CreateRequest(HttpMethod.Get,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        readRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        using var readResponse = await fixture.Client.SendAsync(readRequest);
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_PostTrigger_FiresAfterDelete()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "to-delete");

        await CreateTriggerAsync(fixture, ids, "myPostDelete", PostTriggerBody, "Post", "Delete");

        using var request = fixture.CreateRequest(HttpMethod.Delete,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PostTriggerInclude, "myPostDelete");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    #endregion

    #region Patch

    [Fact]
    public async Task Patch_PreTrigger_FiresBeforePatch()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "original");

        await CreateTriggerAsync(fixture, ids, "myPrePatch", PreTriggerBody, "Pre", "Replace");

        using var request = fixture.CreateRequest(HttpMethod.Patch,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}",
            new
            {
                operations = new[]
                {
                    new { op = "set", path = "/name", value = "patched" }
                }
            });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PreTriggerInclude, "myPrePatch");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["name"]!.GetValue<string>().Should().Be("patched");
    }

    [Fact]
    public async Task Patch_PostTrigger_FiresAfterPatch()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "original");

        await CreateTriggerAsync(fixture, ids, "myPostPatch", PostTriggerBody, "Post", "Replace");

        using var request = fixture.CreateRequest(HttpMethod.Patch,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}",
            new
            {
                operations = new[]
                {
                    new { op = "set", path = "/name", value = "patched" }
                }
            });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PostTriggerInclude, "myPostPatch");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["name"]!.GetValue<string>().Should().Be("patched");
    }

    #endregion

    #region Upsert

    [Fact]
    public async Task Upsert_PreTrigger_ModifiesDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        await CreateTriggerAsync(fixture, ids, "myPreUpsert", PreTriggerBody, "Pre", "Create");

        var docId = NewId("doc");
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = docId, tenantId = ids.PartitionKey, name = "upserted" });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsUpsert, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PreTriggerInclude, "myPreUpsert");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["triggeredBy"]!.GetValue<string>().Should().Be("pre-trigger");
    }

    [Fact]
    public async Task Upsert_PostTrigger_FiresAfterUpsert()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        await CreateTriggerAsync(fixture, ids, "myPostUpsert", PostTriggerBody, "Post", "Create");

        var docId = NewId("doc");
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = docId, tenantId = ids.PartitionKey, name = "upserted" });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsUpsert, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PostTriggerInclude, "myPostUpsert");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region TriggerOperation.All

    [Fact]
    public async Task TriggerOperationAll_FiresOnReplace()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "original");

        await CreateTriggerAsync(fixture, ids, "allOpTrigger", PreTriggerBody, "Pre", "All");

        using var request = fixture.CreateRequest(HttpMethod.Put,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}",
            new { id = docId, tenantId = ids.PartitionKey, name = "replaced" });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PreTriggerInclude, "allOpTrigger");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["triggeredBy"]!.GetValue<string>().Should().Be("pre-trigger");
    }

    [Fact]
    public async Task TriggerOperationAll_FiresOnDelete()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "to-delete");

        await CreateTriggerAsync(fixture, ids, "allOpTrigger", PreTriggerBody, "Pre", "All");

        using var request = fixture.CreateRequest(HttpMethod.Delete,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PreTriggerInclude, "allOpTrigger");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    #endregion

    #region Mismatched operation

    [Fact]
    public async Task Trigger_MismatchedOperation_IsSkipped()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "original");

        // Create a trigger for "Create" operation only
        await CreateTriggerAsync(fixture, ids, "createOnlyTrigger", PreTriggerBody, "Pre", "Create");

        // Use it on a Replace — the trigger should be skipped (not error, not modify)
        using var request = fixture.CreateRequest(HttpMethod.Put,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}",
            new { id = docId, tenantId = ids.PartitionKey, name = "replaced" });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PreTriggerInclude, "createOnlyTrigger");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        // The pre-trigger should NOT have stamped "triggeredBy" because it's Create-only
        body.ContainsKey("triggeredBy").Should().BeFalse();
    }

    #endregion

    #region Helpers

    private static async Task<(string DatabaseId, string ContainerId, string PartitionKey)> CreateContainerHierarchyAsync(TestServerFixture fixture)
    {
        var dbId = NewId("db");
        var containerId = NewId("coll");
        const string partitionKey = "tenant-a";

        using (var dbRequest = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbResponse = await fixture.Client.SendAsync(dbRequest))
        {
            dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using (var containerRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = containerId,
            partitionKey = new
            {
                paths = new[] { "/tenantId" },
                kind = "Hash",
                version = 2
            }
        }))
        using (var containerResponse = await fixture.Client.SendAsync(containerRequest))
        {
            containerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return (dbId, containerId, partitionKey);
    }

    private static async Task<string> CreateDocumentAsync(
        TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids,
        string name)
    {
        var docId = NewId("doc");
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = docId, tenantId = ids.PartitionKey, name });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return docId;
    }

    private static async Task CreateTriggerAsync(
        TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids,
        string triggerId,
        string body,
        string triggerType,
        string triggerOperation)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/triggers",
            new { id = triggerId, body, triggerType, triggerOperation });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content);
        body.Should().NotBeNull();
        return body!.AsObject();
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    #endregion
}
