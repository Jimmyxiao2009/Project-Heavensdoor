using Microsoft.Extensions.Configuration;
using QQReborn.RealServer;

namespace QQReborn.RealServer.Tests;

public class BackendFactoryTests
{
    [Fact]
    public void ResolveBackendId_defaults_to_napcat()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Equal(BackendFactory.NapCat, BackendFactory.ResolveBackendId(cfg));
    }

    [Fact]
    public void ResolveBackendId_maps_aliases_to_napcat()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["QQReborn:Backend"] = "onebot",
        }).Build();
        Assert.Equal(BackendFactory.NapCat, BackendFactory.ResolveBackendId(cfg));
    }
}
