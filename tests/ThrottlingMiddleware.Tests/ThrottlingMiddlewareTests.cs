using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using ThrottlingMiddleware;

namespace ThrottlingMiddleware.Tests;

public class ThrottlingMiddlewareTests
{
    [Fact]
    public async Task Total_rejection_uses_the_rule_period_and_does_not_call_next()
    {
        var rule = Sample.DefaultRule(active: 1, total: 1);
        rule.Period = "hour";
        rule.MaxSleep = TimeSpan.Zero;
        var (middleware, rules, gates, logger, calls) = Pipeline(Sample.Options(rule));
        var gate = gates.Get(rules.Rules.Single(), null);
        Assert.NotNull(gate);
        Assert.Equal(GateDecision.Acquired, await gate.AcquireAsync(CancellationToken.None));

        var context = Http.Context("GET", "/items", IPAddress.Parse("203.0.113.10"));
        await middleware.InvokeAsync(context);

        Assert.Equal(0, calls.Value);
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("3600", context.Response.Headers["Retry-After"].ToString());
        var body = await Http.ReadJson(context);
        Assert.Equal("Too Many Requests", body.GetProperty("title").GetString());
        Assert.Equal(429, body.GetProperty("status").GetInt32());
        Assert.Equal("default", body.GetProperty("rule").GetString());
        Assert.Equal(3600, body.GetProperty("retryAfter").GetInt32());
        Assert.Equal((LogLevel.Debug, "Throttled default because total."), Assert.Single(logger.Entries));
    }

    [Fact]
    public async Task Sleep_rejection_names_the_sleep_reason()
    {
        var rule = Sample.DefaultRule(active: 1, total: 2);
        rule.MaxSleep = TimeSpan.Zero;
        var (middleware, rules, gates, logger, calls) = Pipeline(Sample.Options(rule));
        var gate = gates.Get(rules.Rules.Single(), null);
        Assert.NotNull(gate);
        Assert.Equal(GateDecision.Acquired, await gate.AcquireAsync(CancellationToken.None));

        await middleware.InvokeAsync(Http.Context("GET", "/items"));

        Assert.Equal(0, calls.Value);
        Assert.Equal((LogLevel.Debug, "Throttled default because sleep."), Assert.Single(logger.Entries));
    }

    [Fact]
    public async Task Full_address_table_rejects_without_logging_the_address()
    {
        var options = new ThrottlingOptions
        {
            MaxTrackedIps = 1,
            Rules =
            [
                Sample.DefaultRule(),
                Sample.Remote("by-ip", 1, 1, TimeSpan.Zero),
            ],
        };
        var (middleware, rules, gates, logger, calls) = Pipeline(options);
        var remote = rules.Rules.Single(rule => rule.Name == "by-ip");
        var held = gates.Get(remote, IPAddress.Parse("203.0.113.10"));
        Assert.NotNull(held);
        Assert.Equal(GateDecision.Acquired, await held.AcquireAsync(CancellationToken.None));

        var context = Http.Context("GET", "/items", IPAddress.Parse("203.0.113.11"));
        await middleware.InvokeAsync(context);

        Assert.Equal(0, calls.Value);
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        var body = await Http.ReadJson(context);
        Assert.Equal("by-ip", body.GetProperty("rule").GetString());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal((LogLevel.Debug, "Throttled by-ip because ip-table-full."), entry);
        Assert.DoesNotContain("203.0.113.11", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_rule_does_not_consume_the_default_gate()
    {
        var fallback = Sample.DefaultRule(active: 1, total: 1);
        fallback.MaxSleep = TimeSpan.Zero;
        var options = Sample.Options(
            fallback,
            Sample.Endpoint("orders", "POST /orders", 1, 1, TimeSpan.Zero));
        var (middleware, hold) = Holding(options);

        var first = Http.Context("POST", "/orders");
        var firstTask = middleware.InvokeAsync(first);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Http.Context("POST", "/orders");
        await middleware.InvokeAsync(second).WaitAsync(TimeSpan.FromSeconds(5));
        var other = Http.Context("GET", "/items");
        await middleware.InvokeAsync(other);
        hold.Release.TrySetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.Equal("orders", (await Http.ReadJson(second)).GetProperty("rule").GetString());
        Assert.Equal(StatusCodes.Status200OK, other.Response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_rule_matches_the_route_pattern()
    {
        var options = Sample.Options(
            Sample.DefaultRule(active: 5, total: 5),
            Sample.Endpoint("one-item", "GET /items/{id}", 1, 1, TimeSpan.Zero));
        var (middleware, hold) = Holding(options);

        var first = Http.Context("GET", "/items/5", routePattern: "/items/{id}");
        var firstTask = middleware.InvokeAsync(first);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Http.Context("GET", "/items/9", routePattern: "/items/{id}");
        await middleware.InvokeAsync(second).WaitAsync(TimeSpan.FromSeconds(5));
        hold.Release.TrySetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.Equal("one-item", (await Http.ReadJson(second)).GetProperty("rule").GetString());
    }

    [Fact]
    public async Task Request_path_is_used_when_there_is_no_route_endpoint()
    {
        var options = Sample.Options(
            Sample.DefaultRule(active: 5, total: 5),
            Sample.Endpoint("orders", "POST /orders", 1, 1, TimeSpan.Zero));
        var (middleware, hold) = Holding(options);

        var first = Http.Context("POST", "/orders");
        var firstTask = middleware.InvokeAsync(first);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Http.Context("POST", "/orders");
        await middleware.InvokeAsync(second).WaitAsync(TimeSpan.FromSeconds(5));
        hold.Release.TrySetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.Equal("orders", (await Http.ReadJson(second)).GetProperty("rule").GetString());
    }

    [Fact]
    public async Task A_failed_pipeline_releases_the_gate()
    {
        var rule = Sample.DefaultRule(active: 1, total: 1);
        rule.MaxSleep = TimeSpan.Zero;
        var calls = 0;
        var (middleware, _, _, _, _) = Pipeline(Sample.Options(rule), context =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("pipeline failed");

            return Task.CompletedTask;
        });

        var first = Http.Context("GET", "/items");
        var second = Http.Context("GET", "/items");
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(first));
        await middleware.InvokeAsync(second);

        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);
    }

    private static (ThrottlingMiddleware Middleware, RuleSet Rules, GateRegistry Gates, CaptureLogger Logger, Counter Calls) Pipeline(
        ThrottlingOptions options,
        RequestDelegate? next = null)
    {
        var created = RuleSet.Create(options);
        Assert.Empty(created.Errors);
        var rules = created.Rules!;
        var gates = new GateRegistry(rules);
        var logger = new CaptureLogger();
        var calls = new Counter();
        RequestDelegate pipeline = context =>
        {
            calls.Value++;
            return next is null ? Task.CompletedTask : next(context);
        };

        return (new ThrottlingMiddleware(pipeline, rules, gates, logger), rules, gates, logger, calls);
    }

    private static (ThrottlingMiddleware Middleware, Hold Hold) Holding(ThrottlingOptions options)
    {
        var hold = new Hold();
        var entered = 0;
        var (middleware, _, _, _, _) = Pipeline(options, async _ =>
        {
            if (Interlocked.Increment(ref entered) != 1)
                return;

            hold.Entered.TrySetResult();
            await hold.Release.Task;
        });
        return (middleware, hold);
    }

    private sealed class Hold
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Counter
    {
        public int Value;
    }
}

internal static class Http
{
    public static DefaultHttpContext Context(string method, string path, IPAddress? address = null, string? routePattern = null)
    {
        var context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = address;
        if (routePattern is not null)
        {
            context.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(routePattern),
                0,
                EndpointMetadataCollection.Empty,
                routePattern));
        }

        return context;
    }

    public static async Task<JsonElement> ReadJson(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body));
    }
}

internal sealed class CaptureLogger : ILogger<ThrottlingMiddleware>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
