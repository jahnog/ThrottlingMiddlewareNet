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
    }

    [Fact]
    public void Different_address_returns_a_different_gate()
    {
        var (registry, remote) = Registry(maxTracked: 10);

        Assert.NotSame(
            registry.Get(remote, IPAddress.Parse("203.0.113.10")),
            registry.Get(remote, IPAddress.Parse("203.0.113.11")));
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

    [Fact]
    public async Task Null_address_keeps_a_gate_outside_the_tracked_table()
    {
        var (registry, remote) = Registry(maxTracked: 1);
        var held = registry.Get(remote, IPAddress.Parse("203.0.113.10"));
        Assert.NotNull(held);
        Assert.Equal(GateDecision.Acquired, await held.AcquireAsync(CancellationToken.None));

        var nullAddress = registry.Get(remote, null);
        Assert.NotNull(nullAddress);
        Assert.Same(nullAddress, registry.Get(remote, null));
        Assert.NotSame(held, nullAddress);
        Assert.Null(registry.Get(remote, IPAddress.Parse("203.0.113.11")));
    }

    [Fact]
    public void A_global_rule_uses_one_gate_for_every_address()
    {
        var created = RuleSet.Create(Sample.Options(Sample.DefaultRule()));
        Assert.Empty(created.Errors);
        var rules = created.Rules!;
        var registry = new GateRegistry(rules);
        var global = rules.Rules.Single();
        var first = IPAddress.Parse("203.0.113.10");
        var second = IPAddress.Parse("198.51.100.10");

        Assert.Same(registry.Get(global, first), registry.Get(global, second));
        Assert.Same(registry.Get(global, first), registry.Get(global, null));
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
