namespace Azure.Cosmos.LightEmulator.Auth.ResourceTokens;

public record ResourceToken(string ResourceLink, ResourcePermission Permissions, DateTime ExpiresAt);

public enum ResourcePermission
{
    All,
    Read
}
