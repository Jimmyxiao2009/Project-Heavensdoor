using QQReborn.RealServer.Wire;

namespace QQReborn.RealServer.Tests;

public class WireDispatchTests
{
    [Fact]
    public void KnownTypes_are_unique_and_non_empty()
    {
        Assert.NotEmpty(WireDispatch.KnownTypes);
        Assert.Equal(WireDispatch.KnownTypes.Count, WireDispatch.KnownTypes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("getSelf")]
    [InlineData("send")]
    [InlineData("configureAccount")]
    [InlineData("setGroupAdmin")]
    [InlineData("getSpaceFeed")]
    public void KnownTypes_include_core_routes(string type)
    {
        Assert.Contains(type, WireDispatch.KnownTypes);
    }

    [Fact]
    public void KnownTypes_count_is_stable_floor()
    {
        // Guard against accidental route deletion during refactors.
        Assert.True(WireDispatch.KnownTypes.Count >= 60, "wire surface shrank unexpectedly");
    }
}
