using System.Net;
using ThrottlingMiddleware;

namespace CatalogApi.Tests;

[Collection("CatalogApi")]
public class RouteTests
{
    [Fact]
    public async Task Endpoint_rule_does_not_limit_other_routes()
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 8, total: 16, sleep: TimeSpan.FromSeconds(2), isDefault: true),
            Rules.Endpoint("work", "GET /work", active: 1, total: 1, sleep: TimeSpan.FromSeconds(2)),
            Rules.Remote("by-ip", active: 8, total: 8, sleep: TimeSpan.FromSeconds(2)),
        ]);
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 1000, forwardedFor: "203.0.113.10");

        var again = await client.SendAsync(ApiHost.Work(20, "203.0.113.10"));
        using var items = new HttpRequestMessage(HttpMethod.Get, "/items");
        items.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        var itemsResponse = await client.SendAsync(items);

        await ApiHost.AssertRejectedAsync(again, "work", 60);
        Assert.Equal(HttpStatusCode.OK, itemsResponse.StatusCode);
    }

    [Fact]
    public async Task Forwarded_addresses_do_not_share_a_gate()
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 8, total: 16, sleep: TimeSpan.FromSeconds(2), isDefault: true),
            Rules.Remote("by-ip", active: 1, total: 1, sleep: TimeSpan.FromSeconds(2)),
        ]);
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 1000, forwardedFor: "203.0.113.10");

        var same = await client.SendAsync(ApiHost.Work(20, "203.0.113.10"));
        using var other = await ApiHost.HoldAsync(client, delayMs: 200, forwardedFor: "203.0.113.11");

        await ApiHost.AssertRejectedAsync(same, "by-ip", 60);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Remote_rule_replaces_the_default()
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 1, total: 1, sleep: TimeSpan.FromSeconds(2), isDefault: true),
            Rules.Remote("by-ip", active: 2, total: 2, sleep: TimeSpan.FromSeconds(2)),
        ]);
        using var client = factory.CreateClient();
        using var first = await ApiHost.HoldAsync(client, delayMs: 800, forwardedFor: "203.0.113.10");
        using var second = await ApiHost.HoldAsync(client, delayMs: 800, forwardedFor: "203.0.113.10");

        var third = await client.SendAsync(ApiHost.Work(20, "203.0.113.10"));

        Assert.Equal(2, ApiHost.InFlight(second));
        await ApiHost.AssertRejectedAsync(third, "by-ip", 60);
    }

    [Fact]
    public async Task Unknown_route_uses_the_default_gate()
    {
        await using var open = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 8, total: 16, sleep: TimeSpan.FromSeconds(1), isDefault: true),
        ]);
        using var openClient = open.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await openClient.GetAsync("/missing")).StatusCode);

        await using var tight = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 1, total: 1, sleep: TimeSpan.FromSeconds(2), isDefault: true),
        ]);
        using var tightClient = tight.CreateClient();
        using var held = await ApiHost.HoldAsync(tightClient, delayMs: 1000);

        var missing = await tightClient.GetAsync("/missing");

        await ApiHost.AssertRejectedAsync(missing, "default", 60);
    }

    [Fact]
    public async Task Full_address_table_rejects_until_the_tracked_request_finishes()
    {
        await using var factory = ApiHost.Start(options =>
        {
            options.MaxTrackedIps = 1;
            options.Rules =
            [
                Rules.Global("default", active: 8, total: 16, sleep: TimeSpan.FromSeconds(1), isDefault: true),
                Rules.Remote("by-ip", active: 4, total: 4, sleep: TimeSpan.FromSeconds(1)),
            ];
        });
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 500, forwardedFor: "203.0.113.10");

        var blocked = await client.SendAsync(ApiHost.Work(20, "203.0.113.11"));
        await ApiHost.AssertRejectedAsync(blocked, "by-ip", 60);

        await held.Content.ReadAsByteArrayAsync();
        var admitted = await client.SendAsync(ApiHost.Work(20, "203.0.113.11"));

        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
    }
}
