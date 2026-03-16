using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

public abstract class CosmosControllerBase(CosmosResponseHeaderService responseHeaders) : ControllerBase
{
    protected Task SetCommonHeadersAsync(CosmosResponseHeaderOptions? options = null, CancellationToken ct = default) =>
        responseHeaders.ApplyAsync(Response, options ?? new CosmosResponseHeaderOptions(), ct);
}
