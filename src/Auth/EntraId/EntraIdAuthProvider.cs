using System.IdentityModel.Tokens.Jwt;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Azure.Cosmos.LightEmulator.Auth.EntraId;

/// <summary>
/// EntraID (Azure AD) authentication provider using OIDC/JWT bearer tokens.
/// When TenantId and ClientId are configured, performs full JWT validation
/// (signature, issuer, audience, expiration) against Azure AD metadata.
/// When they are not configured (emulator dev mode), validates JWT structure
/// and expiration only.
/// </summary>
public class EntraIdAuthProvider : IAuthProvider
{
    private readonly string? _tenantId;
    private readonly string? _clientId;
    private readonly bool _enabled;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>>? _configManager;

    public EntraIdAuthProvider(bool enabled, string? tenantId = null, string? clientId = null)
    {
        _enabled = enabled;
        _tenantId = tenantId;
        _clientId = clientId;

        if (_enabled && !string.IsNullOrEmpty(_tenantId))
        {
            var metadataUrl = $"https://login.microsoftonline.com/{_tenantId}/v2.0/.well-known/openid-configuration";
            _configManager = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(() =>
                new ConfigurationManager<OpenIdConnectConfiguration>(
                    metadataUrl,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever()));
        }
    }

    public async Task<AuthResult> ValidateAsync(
        string authHeader,
        string verb,
        string resourceType,
        string resourceLink,
        string dateHeader,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return AuthResult.Failure("EntraID authentication is not enabled.");

        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthResult.Failure("Expected Bearer token for EntraID authentication.");

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return AuthResult.Failure("Empty Bearer token.");

        try
        {
            if (!string.IsNullOrEmpty(_tenantId) && !string.IsNullOrEmpty(_clientId))
                return await ValidateFullAsync(token, ct);

            return ValidateStructureOnly(token);
        }
        catch (SecurityTokenExpiredException)
        {
            return AuthResult.Failure("Bearer token has expired.");
        }
        catch (SecurityTokenException ex)
        {
            return AuthResult.Failure($"Bearer token validation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Full validation: signature, issuer, audience, and expiration via Azure AD OIDC metadata.
    /// </summary>
    private async Task<AuthResult> ValidateFullAsync(string token, CancellationToken ct)
    {
        var config = await _configManager!.Value.GetConfigurationAsync(ct);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = new[]
            {
                $"https://login.microsoftonline.com/{_tenantId}/v2.0",
                $"https://sts.windows.net/{_tenantId}/"
            },
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        var result = await _tokenHandler.ValidateTokenAsync(token, validationParameters);
        if (!result.IsValid)
            return AuthResult.Failure($"Bearer token validation failed: {result.Exception?.Message}");

        var principal = result.ClaimsIdentity.FindFirst("oid")?.Value
                     ?? result.ClaimsIdentity.FindFirst("sub")?.Value
                     ?? "entra-user";

        return AuthResult.Success(AuthType.EntraId, principal);
    }

    /// <summary>
    /// Emulator/dev mode: validates JWT structure and expiration, skips signature/issuer/audience.
    /// </summary>
    private AuthResult ValidateStructureOnly(string token)
    {
        if (!_tokenHandler.CanReadToken(token))
            return AuthResult.Failure("Bearer token is not a valid JWT.");

        var jwt = _tokenHandler.ReadJwtToken(token);

        if (jwt.ValidTo != DateTime.MinValue && jwt.ValidTo < DateTime.UtcNow)
            return AuthResult.Failure("Bearer token has expired.");

        var principal = jwt.Claims.FirstOrDefault(c => c.Type == "oid")?.Value
                     ?? jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value
                     ?? "entra-user";

        return AuthResult.Success(AuthType.EntraId, principal);
    }
}
