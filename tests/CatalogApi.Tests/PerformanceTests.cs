using System.Diagnostics;
using System.Net;
using ThrottlingMiddleware;

namespace CatalogApi.Tests;

[Collection("CatalogApi")]
public class PerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task Parallel_items_stay_under_a_wide_ceiling()
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 400, total: 400, sleep: TimeSpan.FromSeconds(1), isDefault: true),
        ]);
        using var client = factory.CreateClient();

        var started = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, 300).Select(_ => client.GetAsync("/items")));
        started.Stop();

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Saturated_work_rejects_instead_of_hanging()
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 4, total: 4, sleep: TimeSpan.Zero, isDefault: true),
        ]);
        using var client = factory.CreateClient();

        var started = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => client.GetAsync("/work?delayMs=300")));
        started.Stop();

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.OK)
                Assert.InRange(await ApiHost.ReadInFlightAsync(response), 1, 4);
            else
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
                Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
            }
        }

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Two_forwarded_addresses_keep_separate_caps()
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 20, total: 40, sleep: TimeSpan.FromSeconds(1), isDefault: true),
            Rules.Remote("by-ip", active: 2, total: 2, sleep: TimeSpan.Zero),
        ]);
        using var client = factory.CreateClient();

        var first = Send(client, "203.0.113.10");
        var second = Send(client, "203.0.113.11");
        var responses = await Task.WhenAll(first.Concat(second));

        AssertAddress(responses.Take(8).ToArray());
        AssertAddress(responses.Skip(8).Take(8).ToArray());
    }

    private static IEnumerable<Task<HttpResponseMessage>> Send(HttpClient client, string address) =>
        Enumerable.Range(0, 8).Select(_ => client.SendAsync(ApiHost.Work(300, address)));

    private static void AssertAddress(HttpResponseMessage[] responses)
    {
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.All(
            responses.Where(response => response.StatusCode == HttpStatusCode.TooManyRequests),
            response => Assert.Equal("60", response.Headers.GetValues("Retry-After").Single()));
    }
}
