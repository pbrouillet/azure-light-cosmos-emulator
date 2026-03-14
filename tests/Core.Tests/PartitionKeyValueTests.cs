using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.Core.Tests;

public class PartitionKeyValueTests
{
    [Fact]
    public void Create_WithSingleStringValue_CreatesExpectedPartitionKey()
    {
        var partitionKey = PartitionKeyValue.Create("value");

        partitionKey.Components.Should().HaveCount(1);
        partitionKey.Components[0].Should().Be("value");
        partitionKey.ToHeaderString().Should().Be("[\"value\"]");
    }

    [Fact]
    public void Create_WithSingleNumericValue_CreatesExpectedPartitionKey()
    {
        var partitionKey = PartitionKeyValue.Create(123);

        partitionKey.Components.Should().HaveCount(1);
        partitionKey.Components[0].Should().Be(123);
        partitionKey.ToHeaderString().Should().Be("[123]");
    }

    [Fact]
    public void Create_WithNullValue_CreatesExpectedPartitionKey()
    {
        var partitionKey = PartitionKeyValue.Create((object?)null);

        partitionKey.Components.Should().HaveCount(1);
        partitionKey.Components[0].Should().BeNull();
        partitionKey.ToHeaderString().Should().Be("[null]");
    }

    [Fact]
    public void Create_WithMultipleValues_CreatesHierarchicalPartitionKey()
    {
        var partitionKey = PartitionKeyValue.Create("v1", "v2");

        partitionKey.Components.Should().HaveCount(2);
        partitionKey.Components[0].Should().Be("v1");
        partitionKey.Components[1].Should().Be("v2");
        partitionKey.ToHeaderString().Should().Be("[\"v1\",\"v2\"]");
    }

    [Fact]
    public void UndefinedPartitionKey_HasNoComponents()
    {
        PartitionKeyValue.Undefined.Components.Should().BeEmpty();
        PartitionKeyValue.Undefined.ToHeaderString().Should().Be("[]");
    }

    [Fact]
    public void EqualityComparison_ReturnsExpectedResults()
    {
        var first = PartitionKeyValue.Create("value");
        var second = PartitionKeyValue.Create("value");
        var different = PartitionKeyValue.Create("different");

        first.Equals(second).Should().BeTrue();
        first.Equals(different).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_IsConsistentForEqualValues()
    {
        var first = PartitionKeyValue.Create("value", 123, null);
        var second = PartitionKeyValue.Create("value", 123, null);

        first.GetHashCode().Should().Be(second.GetHashCode());
        first.GetHashCode().Should().Be(first.GetHashCode());
    }
}
