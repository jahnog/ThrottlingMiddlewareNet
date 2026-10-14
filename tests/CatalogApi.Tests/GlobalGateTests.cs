using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using ThrottlingMiddleware;

namespace CatalogApi.Tests;

[Collection("CatalogApi")]
public class GlobalGateTests
{
    [Fact]
    public async Task Overlapping_work_reports_two_in_flight()
    {
        await using var factory = Start(active: 2, total: 2, sleep: TimeSpan.FromSeconds(2));
        using var client = factory.CreateClient();
        using var first = await ApiHost.HoldAsync(client, delayMs: 800);

        var second = await client.GetAsync("/work?delayMs=20");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var seen = new[] { ApiHost.InFlight(first), await ApiHost.ReadInFlightAsync(second) };
        Assert.Contains(2, seen);
    }

    [Fact]
    public async Task Second_request_waits_for_an_active_slot()
    {
        await using var factory = Start(active: 1, total: 2, sleep: TimeSpan.FromSeconds(5));
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 1000);

        var started = Stopwatch.StartNew();
        var second = await client.GetAsync("/work?delayMs=20");
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, await ApiHost.ReadInFlightAsync(second));
    }

    [Fact]
    public async Task Full_total_gate_returns_retry_after_without_sleeping()
    {
        await using var factory = Start(active: 1, total: 1, sleep: TimeSpan.FromSeconds(5));
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 1000);

        var started = Stopwatch.StartNew();
        var rejected = await client.GetAsync("/work?delayMs=20");
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500));
        await ApiHost.AssertRejectedAsync(rejected, "default", 60);
    }

    [Fact]
    public async Task Zero_sleep_rejects_without_waiting_for_the_holder()
    {
        await using var factory = Start(active: 1, total: 2, sleep: TimeSpan.Zero);
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 1000);

        var started = Stopwatch.StartNew();
        var rejected = await client.GetAsync("/work?delayMs=20");
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500));
        await ApiHost.AssertRejectedAsync(rejected, "default", 60);
    }

    [Fact]
    public async Task Sleep_timeout_frees_a_total_slot()
    {
        await using var factory = Start(active: 1, total: 2, sleep: TimeSpan.FromMilliseconds(300));
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 2000);

        var waiter = await client.GetAsync("/work?delayMs=20");
        await ApiHost.AssertRejectedAsync(waiter, "default", 60);

        var started = Stopwatch.StartNew();
        var next = await client.GetAsync("/work?delayMs=20");
        started.Stop();

        await ApiHost.AssertRejectedAsync(next, "default", 60);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Completed_work_frees_the_slot()
    {
        await using var factory = Start(active: 1, total: 1, sleep: TimeSpan.Zero);
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/work?delayMs=30");
        var second = await client.GetAsync("/work?delayMs=20");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Cancelling_a_waiter_frees_the_total_slot()
    {
        await using var factory = Start(active: 1, total: 2, sleep: TimeSpan.FromSeconds(5));
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 1500);

        using var cancellation = new CancellationTokenSource();
        var waiting = client.SendAsync(ApiHost.Work(20), HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await Task.Delay(200);
        await cancellation.CancelAsync();
        await ApiHost.AwaitCancelledAsync(waiting);

        var next = await client.GetAsync("/work?delayMs=20");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Aborting_an_admitted_request_frees_the_slot()
    {
        await using var factory = Start(active: 1, total: 1, sleep: TimeSpan.FromSeconds(2));
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var admitted = await client.SendAsync(
            ApiHost.Work(2_000),
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Equal(1, ApiHost.InFlight(admitted));

        // Headers are already complete, so the client token no longer reaches TestServer.
        // Disposing the response aborts the server request.
        await cancellation.CancelAsync();
        admitted.Dispose();

        var started = Stopwatch.StartNew();
        HttpResponseMessage? next = null;
        while (started.Elapsed < TimeSpan.FromSeconds(1))
        {
            next?.Dispose();
            next = await client.GetAsync("/work?delayMs=0");
            if (next.StatusCode == HttpStatusCode.OK)
                break;

            Assert.Equal(HttpStatusCode.TooManyRequests, next.StatusCode);
        }

        Assert.NotNull(next);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Invalid_order_body_frees_the_slot()
    {
        await using var factory = Start(active: 1, total: 1, sleep: TimeSpan.FromSeconds(1));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
        };

        var rejected = await client.SendAsync(request);
        var next = await client.GetAsync("/work?delayMs=20");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Looser_global_rule_replaces_the_default()
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 1, total: 1, sleep: TimeSpan.FromSeconds(2), isDefault: true),
            Rules.Global("loose", active: 2, total: 2, sleep: TimeSpan.FromSeconds(2)),
        ]);
        using var client = factory.CreateClient();
        using var first = await ApiHost.HoldAsync(client, delayMs: 800);

        using var second = await ApiHost.HoldAsync(client, delayMs: 200);

        Assert.Equal(2, ApiHost.InFlight(second));
    }

    [Theory]
    [InlineData("hour", 3600)]
    [InlineData("day", 86400)]
    public async Task Period_is_the_retry_after_value(string period, int retryAfter)
    {
        await using var factory = ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active: 1, total: 1, sleep: TimeSpan.FromSeconds(2), isDefault: true, period: period),
        ]);
        using var client = factory.CreateClient();
        using var held = await ApiHost.HoldAsync(client, delayMs: 1000);

        var rejected = await client.GetAsync("/work?delayMs=20");

        await ApiHost.AssertRejectedAsync(rejected, "default", retryAfter);
    }

    private static WebApplicationFactory<Program> Start(int active, int total, TimeSpan sleep) =>
        ApiHost.Start(options => options.Rules =
        [
            Rules.Global("default", active, total, sleep, isDefault: true),
        ]);
}
