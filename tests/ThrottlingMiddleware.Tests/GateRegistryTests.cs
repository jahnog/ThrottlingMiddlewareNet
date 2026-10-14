using System.Net;
using ThrottlingMiddleware;

namespace ThrottlingMiddleware.Tests;

public class GateRegistryTests
{
    [Fact]
    public void Same_address_returns_the_same_gate()
    {
        var (registry, remote) = Registry(maxTracked: 10);
        var first = IPAddress.Parse("203.0.113.10");
        var second = IPAddress.Parse("203.0.113.10");

        Assert.NotSame(first, second);
        Assert.Same(registry.Get(remote, first), registry.Get(remote, second));
        Assert.NotSame(registry.Get(remote, first), registry.Get(remote, IPAddress.Parse("203.0.113.11")));
    }

    [Fact]
    public async Task Full_table_rejects_a_new_address_until_one_is_idle()
    {
        var (registry, remote) = Registry(maxTracked: 2);
        var firstAddress = IPAddress.Parse("203.0.113.1");
        var secondAddress = IPAddress.Parse("203.0.113.2");
        var thirdAddress = IPAddress.Parse("203.0.113.3");

        var first = registry.Get(remote, firstAddress);
        var second = registry.Get(remote, secondAddress);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(GateDecision.Acquired, await first.AcquireAsync(CancellationToken.None));
        Assert.Equal(GateDecision.Acquired, await second.AcquireAsync(CancellationToken.None));
        Assert.Null(registry.Get(remote, thirdAddress));

        first.Release();
        Assert.True(first.IsIdle);

        var admitted = registry.Get(remote, thirdAddress);
        Assert.NotNull(admitted);
        Assert.NotSame(first, registry.Get(remote, firstAddress));
    }

    private static (GateRegistry Registry, ThrottleRule Remote) Registry(int maxTracked)
    {
        var created = RuleSet.Create(new ThrottlingOptions
        {
            MaxTrackedIps = maxTracked,
            Rules =
            [
                Sample.DefaultRule(),
                Sample.Remote("by-ip", 1, 1, TimeSpan.FromSeconds(1)),
            ],
        });
        Assert.Empty(created.Errors);
        var rules = created.Rules!;
        var remote = rules.Rules.Single(rule => rule.Key == ThrottleKey.RemoteIp);
        return (new GateRegistry(rules), remote);
    }
}
