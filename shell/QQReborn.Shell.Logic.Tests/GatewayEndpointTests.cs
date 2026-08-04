using QQReborn.App.Services;

namespace QQReborn.Shell.Logic.Tests;

public class GatewayEndpointTests
{
    [Theory]
    [InlineData("192.168.1.10", 8765, "192.168.1.10", 8765)]
    [InlineData("example.com:9443", 8765, "example.com", 9443)]
    [InlineData("ws://host:9000/ws", 8765, "host", 9000)]
    [InlineData("http://host/path", 8765, "host", 8765)]
    [InlineData("", 8765, "localhost", 8765)]
    public void NormalizeServerHost_parses_common_forms(string raw, int inPort, string expectHost, int expectPort)
    {
        var port = inPort;
        var host = GatewayEndpoint.NormalizeServerHost(raw, ref port);
        Assert.Equal(expectHost, host);
        Assert.Equal(expectPort, port);
    }

    [Fact]
    public void BuildWsUrl_uses_normalized_host_and_port()
    {
        Assert.Equal("ws://192.168.0.2:1234/ws", GatewayEndpoint.BuildWsUrl("ws://192.168.0.2:1234/ws", 8765));
    }
}
