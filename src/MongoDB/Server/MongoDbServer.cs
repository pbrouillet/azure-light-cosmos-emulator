using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.MongoDB.Server;

/// <summary>
/// TCP server that accepts MongoDB wire protocol connections.
/// </summary>
public class MongoDbServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly ILogger<MongoDbServer> _logger;
    private readonly MongoDbConnectionHandler _connectionHandler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public MongoDbServer(int port, MongoDbConnectionHandler connectionHandler, ILogger<MongoDbServer> logger)
    {
        _port = port;
        _connectionHandler = connectionHandler;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        _logger.LogInformation("MongoDB wire protocol server listening on port {Port}", _port);

        _acceptTask = AcceptConnectionsAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _logger.LogDebug("MongoDB client connected from {Endpoint}", client.Client.RemoteEndPoint);
                // Handle each connection concurrently
                _ = HandleConnectionAsync(client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting MongoDB connection");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var stream = client.GetStream();
            await _connectionHandler.HandleAsync(stream, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error handling MongoDB connection");
        }
        finally
        {
            client.Dispose();
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        if (_acceptTask != null)
        {
            try { await _acceptTask; } catch (OperationCanceledException) { }
        }

        _logger.LogInformation("MongoDB wire protocol server stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
