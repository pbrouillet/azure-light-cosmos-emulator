using Azure.Cosmos.LightEmulator.Core.Consistency;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.Core.Tests;

public class ConsistencyManagerTests
{
    [Fact]
    public void DefaultConsistencyLevel_IsSession()
    {
        var manager = new ConsistencyManager();

        manager.DefaultConsistencyLevel.Should().Be(ConsistencyLevel.Session);
    }

    [Fact]
    public void IsValidConsistencyLevel_AllowsSameOrWeakerLevels()
    {
        var manager = new ConsistencyManager(ConsistencyLevel.Session);

        manager.IsValidConsistencyLevel(ConsistencyLevel.Session).Should().BeTrue();
        manager.IsValidConsistencyLevel(ConsistencyLevel.ConsistentPrefix).Should().BeTrue();
        manager.IsValidConsistencyLevel(ConsistencyLevel.Eventual).Should().BeTrue();
        manager.IsValidConsistencyLevel(ConsistencyLevel.BoundedStaleness).Should().BeFalse();
        manager.IsValidConsistencyLevel(ConsistencyLevel.Strong).Should().BeFalse();
    }

    [Fact]
    public void GetEffectiveConsistency_ReturnsDefaultForNullRequest()
    {
        var manager = new ConsistencyManager(ConsistencyLevel.Session);

        var effective = manager.GetEffectiveConsistency(null);

        effective.Should().Be(ConsistencyLevel.Session);
    }

    [Fact]
    public void GenerateSessionToken_ReturnsExpectedFormat()
    {
        var manager = new ConsistencyManager();

        var token = manager.GenerateSessionToken("db", "container", 42);

        token.Should().Be("0:42");
    }

    [Fact]
    public void ValidateSessionToken_AcceptsValidToken()
    {
        var manager = new ConsistencyManager();
        manager.GenerateSessionToken("db", "container", 42);

        var isValid = manager.ValidateSessionToken("db", "container", "0:42");

        isValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateSessionToken_RejectsInvalidFormat()
    {
        var manager = new ConsistencyManager();
        manager.GenerateSessionToken("db", "container", 42);

        var isValid = manager.ValidateSessionToken("db", "container", "invalid-token");

        isValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateSessionToken_RejectsFutureToken()
    {
        var manager = new ConsistencyManager();
        manager.GenerateSessionToken("db", "container", 42);

        var isValid = manager.ValidateSessionToken("db", "container", "0:43");

        isValid.Should().BeFalse();
    }

    [Fact]
    public void GetCurrentSessionToken_ReturnsZeroTokenForNewContainer()
    {
        var manager = new ConsistencyManager();

        var token = manager.GetCurrentSessionToken("db", "new-container");

        token.Should().Be("0:0");
    }
}
