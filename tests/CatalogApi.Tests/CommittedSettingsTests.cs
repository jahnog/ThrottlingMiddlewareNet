using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using ThrottlingMiddleware;

namespace CatalogApi.Tests;

[Collection("CatalogApi")]
public class CommittedSettingsTests
{
    [Fact]
    public async Task Smoke_uses_the_committed_settings()
    {
        await using var factory = ApiHost.Start();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/items")).StatusCode);

        var order = await client.PostAsJsonAsync("/orders", new { sku = "book", quantity = 1 });
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);
        Assert.EndsWith("/orders/book", order.Headers.Location?.ToString(), StringComparison.Ordinal);
        var created = await order.Content.ReadFromJsonAsync<OrderBody>(ApiHost.Json);
        Assert.Equal("book", created!.Sku);
        Assert.Equal(1, created.Quantity);

        var delayed = await client.GetAsync("/work?delayMs=20");
        Assert.Equal(HttpStatusCode.OK, delayed.StatusCode);
        Assert.Equal(1, await ApiHost.ReadInFlightAsync(delayed));

        var immediate = await client.GetAsync("/work");
        Assert.Equal(HttpStatusCode.OK, immediate.StatusCode);
        Assert.Equal(1, await ApiHost.ReadInFlightAsync(immediate));
    }

    [Fact]
    public async Task Committed_order_rule_rejects_the_fifth_parallel_post()
    {
        await using var factory = ApiHost.Start();
        using var client = factory.CreateClient();
        var orders = Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync("/orders?delayMs=400", new { sku = "book", quantity = 1 }))
            .ToArray();
        var items = client.GetAsync("/items");

        var itemResponse = await items;
        var responses = await Task.WhenAll(orders);

        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                await ApiHost.AssertRejectedAsync(response, "create-order", 60);
            else
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task Negative_delay_returns_without_waiting()
    {
        await using var factory = ApiHost.Start();
        using var client = factory.CreateClient();

        var started = Stopwatch.StartNew();
        var response = await client.GetAsync("/work?delayMs=-5");
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task In_flight_count_is_per_host()
    {
        await using var firstFactory = ApiHost.Start();
        await using var secondFactory = ApiHost.Start();
        using var first = firstFactory.CreateClient();
        using var second = secondFactory.CreateClient();

        using var heldFirst = await ApiHost.HoldAsync(first, delayMs: 800);
        using var heldSecond = await ApiHost.HoldAsync(second, delayMs: 800);

        Assert.Equal(1, ApiHost.InFlight(heldFirst));
        Assert.Equal(1, ApiHost.InFlight(heldSecond));
    }

    [Fact]
    public void Missing_default_fails_host_startup()
    {
        using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Endpoint("work", "GET /work", active: 1, total: 1, sleep: TimeSpan.Zero),
        ]);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.True(ApiHost.IsValidationFailure(exception));
    }

    private sealed record OrderBody(string Sku, int Quantity);
}
