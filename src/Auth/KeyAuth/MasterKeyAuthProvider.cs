using System.Security.Cryptography;
using System.Text;
using Azure.Cosmos.LightEmulator.Core.Interfaces;

namespace Azure.Cosmos.LightEmulator.Auth.KeyAuth;

/// <summary>
/// Validates Cosmos DB master key HMAC-SHA256 authorization headers.
/// See: https://learn.microsoft.com/en-us/rest/api/cosmos-db/access-control-on-cosmosdb-resources
/// </summary>
public class MasterKeyAuthProvider : IAuthProvider
{
    /// <summary>
    /// The well-known default master key used by the Azure Cosmos DB Emulator.
    /// </summary>
    public const string DefaultMasterKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private readonly byte[] _keyBytes;

    public MasterKeyAuthProvider(string masterKey)
    {
        _keyBytes = Convert.FromBase64String(masterKey);
    }

    public Task<AuthResult> ValidateAsync(
        string authHeader,
        string verb,
        string resourceType,
        string resourceLink,
        string dateHeader,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(authHeader))
            return Task.FromResult(AuthResult.Failure("Missing Authorization header."));

        // Parse the auth header: type={type}&ver={version}&sig={signature}
        if (!TryParseAuthHeader(authHeader, out var type, out var version, out var signature))
            return Task.FromResult(AuthResult.Failure("Invalid Authorization header format."));

        if (!string.Equals(type, "master", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthResult.Failure($"Unsupported auth type: {type}"));

        if (!string.Equals(version, "1.0", StringComparison.Ordinal))
            return Task.FromResult(AuthResult.Failure($"Unsupported auth version: {version}"));

        // Compute the expected signature
        var expectedSignature = ComputeSignature(verb, resourceType, resourceLink, dateHeader);

        if (string.Equals(signature, expectedSignature, StringComparison.Ordinal))
            return Task.FromResult(AuthResult.Success(AuthType.MasterKey));

        return Task.FromResult(AuthResult.Failure("Invalid master key signature."));
    }

    /// <summary>
    /// Computes the HMAC-SHA256 signature for a Cosmos DB request.
    /// </summary>
    public string ComputeSignature(string verb, string resourceType, string resourceLink, string date)
    {
        // StringToSign = Verb + "\n" + ResourceType + "\n" + ResourceLink + "\n" + Date + "\n" + "" + "\n"
        var payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceLink}\n{date.ToLowerInvariant()}\n\n";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(_keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Generates a complete authorization header value.
    /// </summary>
    public string GenerateAuthHeader(string verb, string resourceType, string resourceLink, string date)
    {
        var sig = ComputeSignature(verb, resourceType, resourceLink, date);
        return Uri.EscapeDataString($"type=master&ver=1.0&sig={sig}");
    }

    /// <summary>
    /// Generates a new random master key.
    /// </summary>
    public static string GenerateMasterKey()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(keyBytes);
    }

    private static bool TryParseAuthHeader(string header, out string type, out string version, out string signature)
    {
        type = version = signature = string.Empty;

        var decoded = Uri.UnescapeDataString(header);
        var parts = decoded.Split('&');

        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;

            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "type":
                    type = kv[1].Trim();
                    break;
                case "ver":
                    version = kv[1].Trim();
                    break;
                case "sig":
                    signature = kv[1].Trim();
                    break;
            }
        }

        return !string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(signature);
    }
}
