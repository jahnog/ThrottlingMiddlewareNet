using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ThrottlingMiddleware;

namespace ThrottlingMiddleware.Tests;

public class ThrottlingExtensionsTests
{
    [Fact]
    public void AddThrottling_rejects_a_null_service_collection()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ThrottlingExtensions.AddThrottling(null!, _ => { }));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddThrottling_rejects_a_null_configure_action()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddThrottling((Action<ThrottlingOptions>)null!));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void AddThrottling_rejects_a_null_configuration()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddThrottling((IConfiguration)null!));

        Assert.Equal("configuration", exception.ParamName);
    }

    [Fact]
    public void UseThrottling_rejects_a_null_application()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ThrottlingExtensions.UseThrottling(null!));

        Assert.Equal("app", exception.ParamName);
    }

    [Fact]
    public void AddThrottling_rejects_invalid_rules_when_the_rule_set_is_built()
    {
        var services = new ServiceCollection();
        services.AddThrottling(_ => { });
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<RuleSet>());

        Assert.Contains(exception.Failures, failure => failure.Contains("required", StringComparison.Ordinal));
    }

    [Fact]
    public void AddThrottling_binds_the_documented_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MaxTrackedIps"] = "10000",
            ["Rules:0:Name"] = "default",
            ["Rules:0:IsDefault"] = "true",
            ["Rules:0:Key"] = "global",
            ["Rules:0:Period"] = "minute",
            ["Rules:0:MaxActiveRequests"] = "20",
            ["Rules:0:MaxTotalRequests"] = "40",
            ["Rules:0:MaxSleep"] = "00:00:01",
            ["Rules:1:Name"] = "create-order",
            ["Rules:1:Key"] = "endpoint",
            ["Rules:1:Endpoint"] = "POST /orders",
            ["Rules:1:Period"] = "minute",
            ["Rules:1:MaxActiveRequests"] = "2",
            ["Rules:1:MaxTotalRequests"] = "4",
            ["Rules:1:MaxSleep"] = "00:00:01",
        }).Build();
        var services = new ServiceCollection();
        services.AddThrottling(configuration);
        using var provider = services.BuildServiceProvider();

        var rules = provider.GetRequiredService<RuleSet>();

        Assert.Equal(10000, rules.MaxTrackedIps);
        var selected = RuleSet.Select(rules.Rules, "POST", "/orders");
        Assert.Equal("create-order", selected.Name);
        Assert.Equal(2, selected.MaxActiveRequests);
        Assert.Equal(4, selected.MaxTotalRequests);
        Assert.Equal(TimeSpan.FromSeconds(1), selected.MaxSleep);
        Assert.Equal(60, rules.Rules.Single(rule => rule.Name == "default").RetryAfterSeconds);
    }

    [Fact]
    public async Task UseThrottling_limits_one_request_and_then_releases_it()
    {
        var rule = Sample.DefaultRule(active: 1, total: 1);
        rule.MaxSleep = TimeSpan.Zero;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThrottling(options => options.Rules.Add(rule));
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.UseThrottling();
        app.Run(async context =>
        {
            entered.TrySetResult();
            await release.Task;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        var pipeline = app.Build();

        var first = Http.Context("GET", "/items");
        var firstTask = pipeline(first);
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        var second = Http.Context("GET", "/items");
        await pipeline(second).WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        var third = Http.Context("GET", "/items");
        await pipeline(third);

        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.Equal("default", (await Http.ReadJson(second)).GetProperty("rule").GetString());
        Assert.Equal(StatusCodes.Status204NoContent, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, third.Response.StatusCode);
    }
}
