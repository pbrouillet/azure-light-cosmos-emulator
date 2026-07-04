using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.NoSql.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class CosmosExceptionMiddlewareTests
{
    private static DefaultHttpContext CreateContext(out MemoryStream body)
    {
        var context = new DefaultHttpContext();
        body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = "POST";
        context.Request.Path = "/dbs/db/colls/coll/docs";
        return context;
    }

    private static CosmosExceptionMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, NullLogger<CosmosExceptionMiddleware>.Instance);

    [Fact]
    public async Task ClientAbortedCancellation_IsSwallowed_AndRecordsClientClosed()
    {
        // Arrange — a request whose RequestAborted token has fired (client disconnected).
        var context = CreateContext(out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        context.RequestAborted = cts.Token;

        var middleware = CreateMiddleware(_ => throw new OperationCanceledException(cts.Token));

        // Act — must not throw.
        var act = async () => await middleware.InvokeAsync(context);

        // Assert — swallowed, and recorded as 499 (client closed request).
        await act.Should().NotThrowAsync();
        context.Response.StatusCode.Should().Be(499);
    }

    [Fact]
    public async Task Cancellation_NotFromRequestAbort_Propagates()
    {
        // Arrange — RequestAborted has NOT fired; a stray cancellation is a genuine fault.
        var context = CreateContext(out _);
        var middleware = CreateMiddleware(_ => throw new OperationCanceledException());

        // Act
        var act = async () => await middleware.InvokeAsync(context);

        // Assert — must surface (not silently swallowed).
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CosmosEmulatorException_IsMappedToErrorResponse()
    {
        // Arrange
        var context = CreateContext(out var body);
        var middleware = CreateMiddleware(_ => throw CosmosEmulatorException.BadRequest("Bad query."));

        // Act
        await middleware.InvokeAsync(context);

        // Assert — status + JSON error payload.
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(body.ToArray()))!.AsObject();
        payload["code"]!.GetValue<string>().Should().Be("BadRequest");
        payload["message"]!.GetValue<string>().Should().Be("Bad query.");
    }
}
