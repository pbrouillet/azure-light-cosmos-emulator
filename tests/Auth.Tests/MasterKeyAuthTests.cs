using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.Auth.Tests;

public class MasterKeyAuthTests
{
    private const string Verb = "GET";
    private const string ResourceType = "dbs";
    private const string ResourceLink = "dbs/test-db";
    private const string DateHeader = "Tue, 01 Jan 2030 00:00:00 GMT";

    [Fact]
    public async Task ValidSignature_IsAccepted()
    {
        var provider = CreateProvider();
        var signature = provider.ComputeSignature(Verb, ResourceType, ResourceLink, DateHeader);
        var authHeader = Uri.EscapeDataString($"type=master&ver=1.0&sig={signature}");

        var result = await provider.ValidateAsync(authHeader, Verb, ResourceType, ResourceLink, DateHeader);

        result.IsAuthenticated.Should().BeTrue();
        result.AuthType.Should().Be(AuthType.MasterKey);
    }

    [Fact]
    public async Task InvalidSignature_IsRejected()
    {
        var provider = CreateProvider();
        var authHeader = Uri.EscapeDataString("type=master&ver=1.0&sig=invalid-signature");

        var result = await provider.ValidateAsync(authHeader, Verb, ResourceType, ResourceLink, DateHeader);

        result.IsAuthenticated.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid master key signature.");
    }

    [Fact]
    public async Task MissingAuthHeader_Fails()
    {
        var provider = CreateProvider();

        var result = await provider.ValidateAsync(string.Empty, Verb, ResourceType, ResourceLink, DateHeader);

        result.IsAuthenticated.Should().BeFalse();
        result.ErrorMessage.Should().Be("Missing Authorization header.");
    }

    [Fact]
    public async Task WrongAuthType_Fails()
    {
        var provider = CreateProvider();
        var signature = provider.ComputeSignature(Verb, ResourceType, ResourceLink, DateHeader);
        var authHeader = Uri.EscapeDataString($"type=resource&ver=1.0&sig={signature}");

        var result = await provider.ValidateAsync(authHeader, Verb, ResourceType, ResourceLink, DateHeader);

        result.IsAuthenticated.Should().BeFalse();
        result.ErrorMessage.Should().Be("Unsupported auth type: resource");
    }

    [Fact]
    public void GenerateMasterKey_ReturnsValidBase64()
    {
        var masterKey = MasterKeyAuthProvider.GenerateMasterKey();

        var keyBytes = Convert.FromBase64String(masterKey);

        keyBytes.Should().HaveCount(64);
    }

    [Fact]
    public async Task GenerateAuthHeader_RoundTripsThroughValidation()
    {
        var provider = CreateProvider();
        var authHeader = provider.GenerateAuthHeader(Verb, ResourceType, ResourceLink, DateHeader);

        var result = await provider.ValidateAsync(authHeader, Verb, ResourceType, ResourceLink, DateHeader);

        result.IsAuthenticated.Should().BeTrue();
        result.AuthType.Should().Be(AuthType.MasterKey);
    }

    private static MasterKeyAuthProvider CreateProvider()
    {
        var masterKey = MasterKeyAuthProvider.GenerateMasterKey();
        return new MasterKeyAuthProvider(masterKey);
    }
}
