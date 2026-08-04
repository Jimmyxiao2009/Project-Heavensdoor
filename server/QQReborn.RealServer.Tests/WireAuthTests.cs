using QQReborn.RealServer;

namespace QQReborn.RealServer.Tests;

public class WireAuthTests
{
    [Fact]
    public void SafePasswordEquals_matches_same_string()
    {
        Assert.True(WireAuth.SafePasswordEquals("secret", "secret"));
    }

    [Fact]
    public void SafePasswordEquals_rejects_different_string()
    {
        Assert.False(WireAuth.SafePasswordEquals("secret", "Secret"));
        Assert.False(WireAuth.SafePasswordEquals("a", "ab"));
    }

    [Fact]
    public void SafePasswordEquals_handles_null_as_empty()
    {
        Assert.True(WireAuth.SafePasswordEquals(null, null));
        Assert.True(WireAuth.SafePasswordEquals("", null));
        Assert.False(WireAuth.SafePasswordEquals(null, "x"));
    }
}
