using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ThrottlingMiddleware;

namespace CatalogApi.Tests;

[CollectionDefinition("CatalogApi")]
public sealed class CatalogApiCollection;

internal static class ApiHost
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplicationFactory<Program> Start(Action<ThrottlingOptions>? configure = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (configure is null)
                return;

            builder.ConfigureServices(services =>
            {
                services.PostConfigure<ThrottlingOptions>(configure);
            });
        });
    }

    public static HttpRequestMessage Work(int delayMs, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/work?delayMs={delayMs}");
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return request;
    }

    public static async Task<HttpResponseMessage> HoldAsync(HttpClient client, int delayMs, string? forwardedFor = null)
    {
        var response = await client.SendAsync(Work(delayMs, forwardedFor), HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (!response.Headers.Contains("X-In-Flight"))
            throw new InvalidOperationException("The work response did not start.");

        return response;
    }

    public static async Task<int> ReadInFlightAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<WorkBody>(Json);
        return body!.InFlight;
    }

    public static int InFlight(HttpResponseMessage response)
    {
        var raw = response.Headers.GetValues("X-In-Flight").Single();
        return int.Parse(raw, CultureInfo.InvariantCulture);
    }

    public static async Task<RejectionBody> ReadRejectionAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<RejectionBody>(Json);
        return body!;
    }

    public static async Task AssertRejectedAsync(HttpResponseMessage response, string ruleName, int retryAfter)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var header = retryAfter.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(header, response.Headers.GetValues("Retry-After").Single());

        var body = await ReadRejectionAsync(response);
        Assert.Equal("Too Many Requests", body.Title);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, body.Status);
        Assert.Equal(ruleName, body.Rule);
        Assert.Equal(retryAfter, body.RetryAfter);
    }

    public static async Task AwaitCancelledAsync(Task<HttpResponseMessage> pending)
    {
        try
        {
            using var response = await pending;
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static bool IsValidationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OptionsValidationException)
                return true;

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsValidationFailure))
                return true;
        }

        return false;
    }
}

internal sealed record WorkBody(int InFlight);

internal sealed record RejectionBody(string Title, int Status, string Rule, int RetryAfter);
