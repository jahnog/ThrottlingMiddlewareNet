using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using ThrottlingMiddleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddThrottling(builder.Configuration.GetSection("Throttling"));
builder.Services.AddSingleton<WorkTracker>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseRouting();
app.UseThrottling();

app.MapGet("/items", () => new[]
{
    new { sku = "book", name = "Book" },
});

app.MapPost("/orders", async (OrderRequest order, int? delayMs, CancellationToken cancellationToken) =>
{
    var delay = Math.Clamp(delayMs ?? 0, 0, 10_000);
    if (delay > 0)
        await Task.Delay(delay, cancellationToken);

    return Results.Created($"/orders/{order.Sku}", new { order.Sku, order.Quantity });
});

app.MapGet("/work", async (HttpContext context, int? delayMs, WorkTracker tracker, CancellationToken cancellationToken) =>
{
    var delay = Math.Clamp(delayMs ?? 0, 0, 10_000);
    var inFlight = tracker.Enter();
    try
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { inFlight });
        context.Response.Headers["X-In-Flight"] = inFlight.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "application/json";
        await context.Response.StartAsync(cancellationToken);
        await context.Response.Body.WriteAsync(body, cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        if (delay > 0)
            await Task.Delay(delay, cancellationToken);
    }
    finally
    {
        tracker.Exit();
    }
});

app.Run();

public partial class Program;

public sealed class WorkTracker
{
    private int _count;

    public int Enter() => Interlocked.Increment(ref _count);

    public void Exit() => Interlocked.Decrement(ref _count);
}

public sealed record OrderRequest(string Sku, int Quantity);
