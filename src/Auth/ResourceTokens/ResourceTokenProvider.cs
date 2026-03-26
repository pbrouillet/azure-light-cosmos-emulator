using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Cosmos.LightEmulator.Core.Interfaces;

namespace Azure.Cosmos.LightEmulator.Auth.ResourceTokens;

public class ResourceTokenProvider : IAuthProvider
{
    private readonly byte[] _masterKeyBytes;

    public ResourceTokenProvider(string masterKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        _masterKeyBytes = Convert.FromBase64String(masterKey);
    }

    public Task<AuthResult> ValidateAsync(
        string authHeader,
        string verb,
        string resourceType,
        string resourceLink,
        string dateHeader,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!TryExtractToken(authHeader, out var token, out var errorMessage))
            return Task.FromResult(AuthResult.Failure(errorMessage ?? "Invalid resource token."));

        if (!ResourceTokenGenerator.TryValidateToken(token, _masterKeyBytes, out var parsedToken, out errorMessage))
            return Task.FromResult(AuthResult.Failure(errorMessage ?? "Invalid resource token."));

        if (parsedToken.ExpiresAt <= DateTime.UtcNow)
            return Task.FromResult(AuthResult.Failure("Resource token has expired."));

        if (!IsResourceLinkMatch(parsedToken.ResourceLink, resourceLink))
            return Task.FromResult(AuthResult.Failure("Resource token does not grant access to the requested resource."));

        if (!IsPermissionAllowed(parsedToken.Permissions, verb))
            return Task.FromResult(AuthResult.Failure("Resource token does not grant permission for this operation."));

        return Task.FromResult(AuthResult.Success(AuthType.ResourceToken));
    }

    private static bool TryExtractToken(string authHeader, out string token, out string? errorMessage)
    {
        token = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(authHeader))
        {
            errorMessage = "Missing Authorization header.";
            return false;
        }

        var decodedHeader = Uri.UnescapeDataString(authHeader).Trim();
        if (string.IsNullOrWhiteSpace(decodedHeader))
        {
            errorMessage = "Missing Authorization header.";
            return false;
        }

        if (!LooksLikeStructuredAuthHeader(decodedHeader))
        {
            token = decodedHeader;
            return true;
        }

        if (!TryParseAuthHeader(decodedHeader, out var type, out var version, out token))
        {
            errorMessage = "Invalid resource token header format.";
            return false;
        }

        if (!string.Equals(type, "resource", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"Unsupported auth type: {type}";
            return false;
        }

        if (!string.IsNullOrEmpty(version) && !string.Equals(version, "1.0", StringComparison.Ordinal))
        {
            errorMessage = $"Unsupported auth version: {version}";
            return false;
        }

        return true;
    }

    private static bool LooksLikeStructuredAuthHeader(string authHeader) =>
        authHeader.StartsWith("type=", StringComparison.OrdinalIgnoreCase)
        || authHeader.Contains('&', StringComparison.Ordinal);

    private static bool TryParseAuthHeader(string authHeader, out string type, out string version, out string token)
    {
        type = string.Empty;
        version = string.Empty;
        token = string.Empty;

        var parts = authHeader.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var keyValuePair = part.Split('=', 2);
            if (keyValuePair.Length != 2)
                continue;

            switch (keyValuePair[0].Trim().ToLowerInvariant())
            {
                case "type":
                    type = keyValuePair[1].Trim();
                    break;
                case "ver":
                    version = keyValuePair[1].Trim();
                    break;
                case "sig":
                    token = keyValuePair[1].Trim();
                    break;
            }
        }

        return !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(token);
    }

    private static bool IsResourceLinkMatch(string grantedResourceLink, string requestedResourceLink)
    {
        var normalizedGrantedLink = ResourceTokenGenerator.NormalizeResourceLink(grantedResourceLink);
        var normalizedRequestedLink = ResourceTokenGenerator.NormalizeResourceLink(requestedResourceLink, allowEmpty: true);

        if (string.IsNullOrEmpty(normalizedRequestedLink))
            return string.IsNullOrEmpty(normalizedGrantedLink);

        if (string.Equals(normalizedGrantedLink, normalizedRequestedLink, StringComparison.Ordinal))
            return true;

        return normalizedRequestedLink.StartsWith(normalizedGrantedLink + "/", StringComparison.Ordinal);
    }

    private static bool IsPermissionAllowed(ResourcePermission permissions, string verb)
    {
        if (permissions == ResourcePermission.All)
            return true;

        return string.Equals(verb, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "HEAD", StringComparison.OrdinalIgnoreCase);
    }
}

public static class ResourceTokenGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static string GenerateToken(string masterKey, string resourceLink, ResourcePermission permissions, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);

        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "Token TTL must be greater than zero.");

        var masterKeyBytes = Convert.FromBase64String(masterKey);
        var normalizedResourceLink = NormalizeResourceLink(resourceLink);
        var expiresAt = DateTime.UtcNow.Add(ttl);
        var signature = ComputeSignature(masterKeyBytes, normalizedResourceLink, permissions, expiresAt);

        var payload = new ResourceTokenPayload
        {
            ResourceLink = normalizedResourceLink,
            Permissions = permissions.ToString(),
            ExpiresAt = expiresAt,
            Signature = signature
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static ResourceToken ParseToken(string token)
    {
        var payload = DecodePayload(token);
        return CreateResourceToken(payload);
    }

    internal static bool TryValidateToken(string token, byte[] masterKeyBytes, out ResourceToken resourceToken, out string? errorMessage)
    {
        resourceToken = default!;
        errorMessage = null;

        try
        {
            var payload = DecodePayload(token);
            resourceToken = CreateResourceToken(payload);
            var expectedSignature = ComputeSignature(masterKeyBytes, resourceToken.ResourceLink, resourceToken.Permissions, resourceToken.ExpiresAt);

            if (!FixedTimeEquals(payload.Signature, expectedSignature))
            {
                errorMessage = "Invalid resource token signature.";
                return false;
            }

            return true;
        }
        catch (FormatException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (JsonException)
        {
            errorMessage = "Invalid resource token payload.";
            return false;
        }
    }

    internal static string NormalizeResourceLink(string? resourceLink, bool allowEmpty = false)
    {
        var normalized = (resourceLink ?? string.Empty).Trim().Trim('/').ToLowerInvariant();

        if (!allowEmpty && string.IsNullOrWhiteSpace(normalized))
            throw new FormatException("Resource token resourceLink is missing.");

        return normalized;
    }

    private static ResourceTokenPayload DecodePayload(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new FormatException("Resource token is missing.");

        string json;
        try
        {
            var payloadBytes = Convert.FromBase64String(token.Trim());
            json = Encoding.UTF8.GetString(payloadBytes);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Resource token is not valid Base64.", ex);
        }

        var payload = JsonSerializer.Deserialize<ResourceTokenPayload>(json, JsonOptions)
            ?? throw new FormatException("Resource token payload is missing.");

        if (string.IsNullOrWhiteSpace(payload.ResourceLink))
            throw new FormatException("Resource token resourceLink is missing.");

        if (string.IsNullOrWhiteSpace(payload.Permissions))
            throw new FormatException("Resource token permissions are missing.");

        if (string.IsNullOrWhiteSpace(payload.Signature))
            throw new FormatException("Resource token signature is missing.");

        return payload;
    }

    private static ResourceToken CreateResourceToken(ResourceTokenPayload payload)
    {
        if (!Enum.TryParse<ResourcePermission>(payload.Permissions, ignoreCase: true, out var permissions))
            throw new FormatException($"Unsupported resource token permissions: {payload.Permissions}");

        return new ResourceToken(
            NormalizeResourceLink(payload.ResourceLink),
            permissions,
            EnsureUtc(payload.ExpiresAt));
    }

    private static string ComputeSignature(byte[] masterKeyBytes, string resourceLink, ResourcePermission permissions, DateTime expiresAt)
    {
        var payload = $"{NormalizeResourceLink(resourceLink)}\n{permissions}\n{EnsureUtc(expiresAt):O}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(masterKeyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToBase64String(hash);
    }

    private static bool FixedTimeEquals(string providedSignature, string expectedSignature)
    {
        try
        {
            var providedBytes = Convert.FromBase64String(providedSignature);
            var expectedBytes = Convert.FromBase64String(expectedSignature);
            return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DateTime EnsureUtc(DateTime timestamp) =>
        timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

    private sealed class ResourceTokenPayload
    {
        public string ResourceLink { get; init; } = string.Empty;

        public string Permissions { get; init; } = string.Empty;

        public DateTime ExpiresAt { get; init; }

        public string Signature { get; init; } = string.Empty;
    }
}
