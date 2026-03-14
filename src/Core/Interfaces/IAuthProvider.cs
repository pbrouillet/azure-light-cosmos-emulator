namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Authentication provider for validating Cosmos DB requests.
/// </summary>
public interface IAuthProvider
{
    /// <summary>
    /// Validates an authorization header and returns the authenticated identity.
    /// </summary>
    /// <param name="authHeader">The Authorization header value.</param>
    /// <param name="verb">HTTP verb (GET, POST, etc.).</param>
    /// <param name="resourceType">Resource type (dbs, colls, docs, etc.).</param>
    /// <param name="resourceLink">Resource link path.</param>
    /// <param name="dateHeader">The x-ms-date or Date header value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The authenticated identity.</returns>
    Task<AuthResult> ValidateAsync(
        string authHeader,
        string verb,
        string resourceType,
        string resourceLink,
        string dateHeader,
        CancellationToken ct = default);
}

/// <summary>
/// Result of authentication validation.
/// </summary>
public class AuthResult
{
    /// <summary>Whether authentication succeeded.</summary>
    public required bool IsAuthenticated { get; init; }

    /// <summary>The authentication type used.</summary>
    public AuthType AuthType { get; init; }

    /// <summary>Error message if authentication failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The authenticated principal (for EntraID).</summary>
    public string? Principal { get; init; }

    public static AuthResult Success(AuthType type, string? principal = null) =>
        new() { IsAuthenticated = true, AuthType = type, Principal = principal };

    public static AuthResult Failure(string message) =>
        new() { IsAuthenticated = false, ErrorMessage = message };
}

public enum AuthType
{
    MasterKey,
    ResourceToken,
    EntraId
}
