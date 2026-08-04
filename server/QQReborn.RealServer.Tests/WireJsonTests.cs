using System.Text.Json.Nodes;
using QQReborn.RealServer.Wire;

namespace QQReborn.RealServer.Tests;

/// <summary>
/// Guards wire flag parsing used by group admin / reactions.
/// Must stay compatible with historical Shell payloads (bool / number / string).
/// </summary>
public class WireJsonTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Flag_reads_bool(bool value, bool expected)
    {
        var o = new JsonObject { ["isAdd"] = value };
        Assert.Equal(expected, WireJson.Flag(o, "isAdd", defaultValue: true));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void Flag_reads_number(double value, bool expected)
    {
        var o = new JsonObject { ["isAdd"] = value };
        Assert.Equal(expected, WireJson.Flag(o, "isAdd", defaultValue: true));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    public void Flag_reads_string_like_legacy_isAdd(string value, bool expected)
    {
        var o = new JsonObject { ["isAdd"] = value };
        Assert.Equal(expected, WireJson.Flag(o, "isAdd", defaultValue: true));
    }

    [Fact]
    public void Flag_missing_key_uses_default()
    {
        var o = new JsonObject();
        Assert.True(WireJson.Flag(o, "isAdd", defaultValue: true));
        Assert.False(WireJson.Flag(o, "enable", defaultValue: false));
    }
}
