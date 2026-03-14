using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.Core.Tests;

public class ResourceIdTests
{
    [Fact]
    public void ResourceIdGenerate_ReturnsNonEmptyString()
    {
        var resourceId = ResourceId.Generate();

        resourceId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ResourceIdGenerate_ReturnsUniqueValues()
    {
        var resourceIds = Enumerable.Range(0, 10)
            .Select(_ => ResourceId.Generate())
            .ToArray();

        resourceIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ETagGeneratorGenerate_ReturnsQuotedHexString()
    {
        var etag = ETagGenerator.Generate();

        etag.Should().MatchRegex("^\"[0-9a-f]{16}\"$");
    }

    [Fact]
    public void ETagGeneratorGenerate_ReturnsUniqueValues()
    {
        var etags = Enumerable.Range(0, 10)
            .Select(_ => ETagGenerator.Generate())
            .ToArray();

        etags.Should().OnlyHaveUniqueItems();
    }
}
