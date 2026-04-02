using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Azure.Cosmos.LightEmulator.Host.Tests;

public class TtlCleanupServiceTests
{
    [Fact]
    public async Task StopAsync_SuppressesObjectDisposedExceptionFromCtsCallbacks()
    {
        // Simulate the SurrealDB client pattern: operations register CTS callbacks on the
        // stoppingToken that later throw ObjectDisposedException during shutdown because
        // the linked CTS has been disposed before the callback fires.
        var documentStore = new Mock<IDocumentStore>();
        documentStore
            .Setup(x => x.ListDatabasesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                // Register callbacks that simulate disposed linked CTS.
                // When stoppingToken is canceled (during StopAsync), these callbacks fire
                // and throw ObjectDisposedException — matching the production scenario.
                for (var i = 0; i < 5; i++)
                {
                    ct.Register(() =>
                    {
                        var disposed = new CancellationTokenSource();
                        disposed.Dispose();
                        disposed.Cancel(); // Throws ObjectDisposedException
                    });
                }

                return Task.FromResult(new FeedResponse<CosmosDatabase> { Resources = [] });
            });

        var consistencyManager = new Mock<IConsistencyManager>();
        var ruTracker = new RuTracker();
        var logger = NullLogger<TtlCleanupService>.Instance;

        using var service = new TtlCleanupService(
            documentStore.Object, consistencyManager.Object, ruTracker, logger);

        // Start the service — this creates _stoppingCts and calls ExecuteAsync(stoppingToken).
        // ExecuteAsync immediately calls RunCleanupAsync which registers the bad callbacks.
        await service.StartAsync(CancellationToken.None);

        // Give ExecuteAsync a moment to run the first cleanup iteration
        await Task.Delay(200);

        // StopAsync cancels _stoppingCts, which fires the stale callbacks.
        // Without the fix, this would throw AggregateException with ObjectDisposedException.
        var act = () => service.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_DoesNotSuppressOtherExceptions()
    {
        // Verify that non-ObjectDisposedException errors from CTS callbacks are NOT suppressed.
        var documentStore = new Mock<IDocumentStore>();
        documentStore
            .Setup(x => x.ListDatabasesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                ct.Register(() => throw new InvalidOperationException("real error"));
                return Task.FromResult(new FeedResponse<CosmosDatabase> { Resources = [] });
            });

        var consistencyManager = new Mock<IConsistencyManager>();
        var ruTracker = new RuTracker();
        var logger = NullLogger<TtlCleanupService>.Instance;

        using var service = new TtlCleanupService(
            documentStore.Object, consistencyManager.Object, ruTracker, logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        var act = () => service.StopAsync(CancellationToken.None);
        await act.Should().ThrowAsync<AggregateException>();
    }
}
