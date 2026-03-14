using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Azure.Cosmos;

namespace Azure.Cosmos.LightEmulator.Parity;

public class SmokeTests : ParityTestBase
{
    [Fact]
    public async Task CreateDatabase_ShouldSucceed()
    {
        var databaseId = $"db-{Guid.NewGuid():N}";

        var createResponse = await Client.CreateDatabaseAsync(databaseId);
        var readResponse = await createResponse.Database.ReadAsync();

        readResponse.Resource.Id.Should().Be(databaseId);
    }

    [Fact]
    public async Task CreateContainer_ShouldSucceed()
    {
        var databaseId = $"db-{Guid.NewGuid():N}";
        var containerId = $"coll-{Guid.NewGuid():N}";
        var database = await Client.CreateDatabaseAsync(databaseId);

        var createResponse = await database.Database.CreateContainerAsync(
            new ContainerProperties(containerId, "/partitionKey"));
        var readResponse = await database.Database.GetContainer(containerId).ReadContainerAsync();

        readResponse.Resource.Id.Should().Be(containerId);
        readResponse.Resource.PartitionKeyPath.Should().Be("/partitionKey");
        createResponse.Resource.Id.Should().Be(containerId);
    }

    [Fact]
    public async Task CrudDocument_ShouldSucceed()
    {
        var databaseId = $"db-{Guid.NewGuid():N}";
        var containerId = $"coll-{Guid.NewGuid():N}";
        var database = await Client.CreateDatabaseAsync(databaseId);
        await database.Database.CreateContainerAsync(
            new ContainerProperties(containerId, "/partitionKey"));

        var document = new TestDocument(Guid.NewGuid().ToString("N"), "tenant-1", "created");
        var replacement = document with { value = "updated" };
        var resourceLink = $"dbs/{databaseId}/colls/{containerId}";
        var documentResourceLink = $"{resourceLink}/docs/{document.id}";
        var partitionHeaders = new Dictionary<string, string>
        {
            ["x-ms-documentdb-partitionkey"] = $"[\"{document.partitionKey}\"]"
        };

        var createResponse = await SendAuthorizedAsync(HttpMethod.Post, $"/{resourceLink}/docs", "docs", resourceLink, document);
        var readResponse = await SendAuthorizedAsync(HttpMethod.Get, $"/{documentResourceLink}", "docs", documentResourceLink, headers: partitionHeaders);
        var replaceResponse = await SendAuthorizedAsync(HttpMethod.Put, $"/{documentResourceLink}", "docs", documentResourceLink, replacement);
        var deleteResponse = await SendAuthorizedAsync(HttpMethod.Delete, $"/{documentResourceLink}", "docs", documentResourceLink, headers: partitionHeaders);
        var deletedReadResponse = await SendAuthorizedAsync(HttpMethod.Get, $"/{documentResourceLink}", "docs", documentResourceLink, headers: partitionHeaders);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        deletedReadResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var createdDocument = await createResponse.Content.ReadFromJsonAsync<JsonObject>();
        var readDocument = await readResponse.Content.ReadFromJsonAsync<JsonObject>();
        var replacedDocument = await replaceResponse.Content.ReadFromJsonAsync<JsonObject>();

        createdDocument!["id"]!.GetValue<string>().Should().Be(document.id);
        readDocument!["value"]!.GetValue<string>().Should().Be("created");
        replacedDocument!["value"]!.GetValue<string>().Should().Be("updated");
    }

    private sealed record TestDocument(string id, string partitionKey, string value);
}
