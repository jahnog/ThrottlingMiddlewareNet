using ThrottlingMiddleware;

namespace ThrottlingMiddleware.Tests;

public class RuleSetTests
{
    [Fact]
    public void Create_accepts_remote_ip_and_periods()
    {
        var created = RuleSet.Create(new ThrottlingOptions
        {
            Rules =
            [
                Sample.DefaultRule(),
                Sample.Remote("by-ip", 2, 4, TimeSpan.FromSeconds(1), "hour"),
                Sample.Endpoint("orders", "POST /orders", 1, 2, TimeSpan.FromMilliseconds(5), "day"),
            ],
        });

        Assert.Empty(created.Errors);
        var rules = created.Rules!.Rules;
        Assert.Equal(3600, rules.Single(rule => rule.Name == "by-ip").RetryAfterSeconds);
        Assert.Equal(86400, rules.Single(rule => rule.Name == "orders").RetryAfterSeconds);
        Assert.Equal(60, rules.Single(rule => rule.Name == "default").RetryAfterSeconds);
    }

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void Create_rejects_invalid_rules(ThrottlingOptions options, string fragment)
    {
        var created = RuleSet.Create(options);

        Assert.Null(created.Rules);
        Assert.Contains(created.Errors, error => error.Contains(fragment, StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> InvalidRules()
    {
        var secondDefault = Sample.DefaultRule();
        secondDefault.Name = "other";
        yield return [Sample.Options(Sample.DefaultRule(), secondDefault), "Only one"];
        yield return [Sample.Options(Sample.Endpoint("only", "GET /work", 1, 1, TimeSpan.Zero)), "required"];
        yield return [new ThrottlingOptions(), "required"];

        var notGlobal = Sample.DefaultRule();
        notGlobal.Key = "endpoint";
        notGlobal.Endpoint = "GET /work";
        yield return [Sample.Options(notGlobal), "global"];

        var hyphen = Sample.Remote("by-ip", 1, 1, TimeSpan.Zero);
        hyphen.Key = "remote-ip";
        yield return [Sample.Options(Sample.DefaultRule(), hyphen), "unknown key"];

        var period = Sample.DefaultRule();
        period.Period = "week";
        yield return [Sample.Options(period), "unknown period"];

        yield return [Sample.Options(Sample.DefaultRule(), Sample.Endpoint("default", "GET /work", 1, 1, TimeSpan.Zero)), "Duplicate"];

        var empty = Sample.DefaultRule();
        empty.Name = " ";
        yield return [Sample.Options(empty), "empty"];

        yield return [Sample.Options(Sample.DefaultRule(), Sample.Endpoint("work", "", 1, 1, TimeSpan.Zero)), "METHOD /path"];

        var active = Sample.DefaultRule();
        active.MaxActiveRequests = 0;
        yield return [Sample.Options(active), "maxActiveRequests"];

        var total = Sample.DefaultRule();
        total.MaxActiveRequests = 2;
        total.MaxTotalRequests = 1;
        yield return [Sample.Options(total), "below"];

        var sleep = Sample.DefaultRule();
        sleep.MaxSleep = TimeSpan.FromMilliseconds(-1);
        yield return [Sample.Options(sleep), "negative"];

        yield return [new ThrottlingOptions { MaxTrackedIps = 0, Rules = [Sample.DefaultRule()] }, "MaxTrackedIps"];
    }

    [Fact]
    public void Select_prefers_endpoint_then_remote_then_global_then_default()
    {
        var created = RuleSet.Create(Sample.Options(
            Sample.DefaultRule(active: 20, total: 40),
            Sample.Global("tighter-global", 5, 10, TimeSpan.FromSeconds(1)),
            Sample.Remote("by-ip", 3, 6, TimeSpan.FromSeconds(1)),
            Sample.Endpoint("orders", "POST /orders", 1, 2, TimeSpan.FromSeconds(1))));
        var rules = created.Rules!.Rules;

        Assert.Equal("orders", RuleSet.Select(rules, "POST", "/orders").Name);
        Assert.Equal("by-ip", RuleSet.Select(rules, "GET", "/items").Name);
    }

    [Fact]
    public void Select_uses_global_when_no_remote_rule_matches_first()
    {
        var rules = RuleSet.Create(Sample.Options(
            Sample.DefaultRule(active: 20, total: 40),
            Sample.Global("tighter-global", 5, 10, TimeSpan.FromSeconds(1)))).Rules!.Rules;

        Assert.Equal("tighter-global", RuleSet.Select(rules, "GET", "/items").Name);
    }

    [Fact]
    public void Select_uses_default_when_it_is_the_only_match()
    {
        var rules = RuleSet.Create(Sample.Options(
            Sample.DefaultRule(),
            Sample.Endpoint("orders", "POST /orders", 1, 1, TimeSpan.Zero))).Rules!.Rules;

        Assert.Equal("default", RuleSet.Select(rules, "GET", "/items").Name);
    }

    [Fact]
    public void Select_picks_the_tighter_endpoint_and_ignores_case()
    {
        var rules = RuleSet.Create(Sample.Options(
            Sample.DefaultRule(),
            Sample.Endpoint("loose", "GET /work", 5, 9, TimeSpan.FromSeconds(2)),
            Sample.Endpoint("tight", "get /work", 4, 9, TimeSpan.FromSeconds(3)),
            Sample.Endpoint("tighter-total", "GET /WORK", 1, 3, TimeSpan.FromSeconds(4)))).Rules!.Rules;

        Assert.Equal("tighter-total", RuleSet.Select(rules, "get", "/work").Name);
    }

    [Fact]
    public void Select_breaks_remaining_ties_by_sleep_then_name()
    {
        var rules = RuleSet.Create(Sample.Options(
            Sample.DefaultRule(),
            Sample.Endpoint("zeta", "GET /work", 2, 4, TimeSpan.FromSeconds(2)),
            Sample.Endpoint("alpha", "GET /work", 2, 4, TimeSpan.FromSeconds(2)),
            Sample.Endpoint("short", "GET /work", 2, 4, TimeSpan.FromMilliseconds(10)))).Rules!.Rules;

        Assert.Equal("short", RuleSet.Select(rules, "GET", "/work").Name);

        var named = RuleSet.Create(Sample.Options(
            Sample.DefaultRule(),
            Sample.Endpoint("zeta", "GET /work", 2, 4, TimeSpan.FromSeconds(2)),
            Sample.Endpoint("alpha", "GET /work", 2, 4, TimeSpan.FromSeconds(2)))).Rules!.Rules;

        Assert.Equal("alpha", RuleSet.Select(named, "GET", "/work").Name);
    }
}
