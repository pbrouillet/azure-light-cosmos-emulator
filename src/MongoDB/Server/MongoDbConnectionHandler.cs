using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.MongoDB.Server;

/// <summary>
/// Handles individual MongoDB client connections by reading wire protocol messages.
/// </summary>
public class MongoDbConnectionHandler
{
    // Matches the maxMessageSizeBytes advertised in the isMaster/hello response.
    // Prevents a malformed or hostile frame from triggering an arbitrarily large
    // (up to ~2 GB) buffer allocation off a single wire-supplied length field.
    private const int MaxMessageLength = 48_000_000;

    private readonly ILogger<MongoDbConnectionHandler> _logger;

    public MongoDbConnectionHandler(ILogger<MongoDbConnectionHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBuffer = new byte[16]; // MongoDB message header is 16 bytes

        while (!ct.IsCancellationRequested)
        {
            // Read the 16-byte message header
            var bytesRead = await ReadExactAsync(stream, headerBuffer, ct);
            if (bytesRead == 0)
                break; // Connection closed

            var messageLength = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(0, 4));
            var requestId = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(4, 4));
            var responseTo = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(8, 4));
            var opCode = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(12, 4));

            _logger.LogDebug("MongoDB message: length={Length}, requestId={RequestId}, opCode={OpCode}",
                messageLength, requestId, opCode);

            // Validate the wire-supplied length before allocating. A message must be at
            // least the header size and no larger than the advertised maximum. Anything
            // else indicates a corrupt frame or a non-MongoDB client; close the connection.
            if (messageLength < 16 || messageLength > MaxMessageLength)
            {
                _logger.LogWarning(
                    "MongoDB message length {Length} is out of range (16..{Max}); closing connection.",
                    messageLength, MaxMessageLength);
                break;
            }

            // Read the message body
            var bodyLength = messageLength - 16;
            if (bodyLength == 0) continue;

            var bodyBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bodyLength);
            try
            {
                var bodySpan = bodyBuffer.AsMemory(0, bodyLength);
                if (await ReadExactAsync(stream, bodySpan, ct) < bodyLength)
                    break; // Connection closed mid-message

                // Process based on opCode
                var response = opCode switch
                {
                    2013 => await HandleOpMsg(requestId, bodyBuffer, ct),   // OP_MSG
                    2004 => await HandleOpQuery(requestId, bodyBuffer, ct), // OP_QUERY (legacy)
                    _ => CreateErrorResponse(requestId, $"Unsupported opCode: {opCode}")
                };

                // Send response
                await stream.WriteAsync(response, ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(bodyBuffer);
            }
        }
    }

    private Task<byte[]> HandleOpMsg(int requestId, byte[] body, CancellationToken ct)
    {
        // TODO: Parse OP_MSG sections, extract command, dispatch to handlers
        // For now, return a basic "ok" response
        return Task.FromResult(CreateOpMsgResponse(requestId, """{ "ok": 1 }"""));
    }

    private Task<byte[]> HandleOpQuery(int requestId, byte[] body, CancellationToken ct)
    {
        // TODO: Parse OP_QUERY, handle isMaster/hello commands
        // For now, return a basic isMaster response
        var response = """
        {
            "ismaster": true,
            "maxBsonObjectSize": 16777216,
            "maxMessageSizeBytes": 48000000,
            "maxWriteBatchSize": 100000,
            "ok": 1
        }
        """;
        return Task.FromResult(CreateOpReplyResponse(requestId, response));
    }

    private static byte[] CreateOpMsgResponse(int responseTo, string json)
    {
        var bsonDoc = System.Text.Encoding.UTF8.GetBytes(json);
        // Simplified: in production, use proper BSON serialization
        var flagBytes = new byte[4]; // flags = 0
        var sectionKind = new byte[] { 0 }; // kind 0 = body

        // BSON document wrapper (simplified — real impl should use MongoDB.Bson)
        var bsonLength = bsonDoc.Length + 5; // 4-byte length + content + null terminator
        var bsonWrapper = new byte[bsonLength];
        BinaryPrimitives.WriteInt32LittleEndian(bsonWrapper, bsonLength);
        Buffer.BlockCopy(bsonDoc, 0, bsonWrapper, 4, bsonDoc.Length);

        var bodyLength = flagBytes.Length + sectionKind.Length + bsonWrapper.Length;
        var totalLength = 16 + bodyLength;

        var response = new byte[totalLength];
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(0), totalLength);    // messageLength
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4), 0);              // requestId
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8), responseTo);     // responseTo
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(12), 2013);          // opCode = OP_MSG

        var offset = 16;
        Buffer.BlockCopy(flagBytes, 0, response, offset, flagBytes.Length);
        offset += flagBytes.Length;
        Buffer.BlockCopy(sectionKind, 0, response, offset, sectionKind.Length);
        offset += sectionKind.Length;
        Buffer.BlockCopy(bsonWrapper, 0, response, offset, bsonWrapper.Length);

        return response;
    }

    private static byte[] CreateOpReplyResponse(int responseTo, string json)
    {
        // OP_REPLY format (legacy, for OP_QUERY responses)
        var bsonDoc = System.Text.Encoding.UTF8.GetBytes(json);
        var bsonLength = bsonDoc.Length + 5;
        var bsonWrapper = new byte[bsonLength];
        BinaryPrimitives.WriteInt32LittleEndian(bsonWrapper, bsonLength);
        Buffer.BlockCopy(bsonDoc, 0, bsonWrapper, 4, bsonDoc.Length);

        // OP_REPLY header: flags(4) + cursorId(8) + startingFrom(4) + numberReturned(4) = 20
        var bodyLength = 20 + bsonWrapper.Length;
        var totalLength = 16 + bodyLength;

        var response = new byte[totalLength];
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(0), totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8), responseTo);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(12), 1);  // opCode = OP_REPLY

        var offset = 16;
        // responseFlags = 0
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(offset), 0);
        offset += 4;
        // cursorId = 0
        BinaryPrimitives.WriteInt64LittleEndian(response.AsSpan(offset), 0);
        offset += 8;
        // startingFrom = 0
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(offset), 0);
        offset += 4;
        // numberReturned = 1
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(offset), 1);
        offset += 4;

        Buffer.BlockCopy(bsonWrapper, 0, response, offset, bsonWrapper.Length);

        return response;
    }

    private static byte[] CreateErrorResponse(int responseTo, string error)
    {
        return CreateOpMsgResponse(responseTo, $$"""{ "ok": 0, "errmsg": "{{error}}" }""");
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0) return totalRead; // Connection closed
            totalRead += read;
        }
        return totalRead;
    }
}
