using System.Net;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

/// <summary>
/// Returns 410 Gone for all attachment operations. Attachments are deprecated in Azure Cosmos DB.
/// </summary>
[ApiController]
[Route("dbs/{dbId}/colls/{collId}/docs/{docId}/attachments")]
public class AttachmentsController : CosmosControllerBase
{
    private const string DeprecationMessage =
        "Attachments are deprecated in Azure Cosmos DB and are not supported by this emulator. " +
        "Use Azure Blob Storage for binary data. " +
        "See: https://learn.microsoft.com/en-us/azure/cosmos-db/attachments";

    public AttachmentsController(CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
    }

    [HttpGet]
    [HttpPost]
    public IActionResult HandleCollection() => GoneResponse();

    [HttpGet("{attachmentId}")]
    [HttpPut("{attachmentId}")]
    [HttpDelete("{attachmentId}")]
    public IActionResult HandleItem(string attachmentId) => GoneResponse();

    private ObjectResult GoneResponse() =>
        StatusCode((int)HttpStatusCode.Gone, new { code = "Gone", message = DeprecationMessage });
}
