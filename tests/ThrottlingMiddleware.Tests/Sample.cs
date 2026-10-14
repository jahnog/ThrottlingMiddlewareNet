using ThrottlingMiddleware;

namespace ThrottlingMiddleware.Tests;

internal static class Sample
{
    public static ThrottlingOptions Options(params ThrottleRuleOptions[] rules) => new()
    {
        Rules = [.. rules],
    };

    public static ThrottleRuleOptions DefaultRule(int active = 8, int total = 16) => new()
    {
        Name = "default",
        IsDefault = true,
        Key = "global",
        Period = "minute",
        MaxActiveRequests = active,
        MaxTotalRequests = total,
        MaxSleep = TimeSpan.FromSeconds(1),
    };

    public static ThrottleRuleOptions Global(string name, int active, int total, TimeSpan sleep) => new()
    {
        Name = name,
        Key = "global",
        Period = "minute",
        MaxActiveRequests = active,
        MaxTotalRequests = total,
        MaxSleep = sleep,
    };

    public static ThrottleRuleOptions Endpoint(
        string name,
        string endpoint,
        int active,
        int total,
        TimeSpan sleep,
        string period = "minute") => new()
    {
        Name = name,
        Key = "endpoint",
        Endpoint = endpoint,
        Period = period,
        MaxActiveRequests = active,
        MaxTotalRequests = total,
        MaxSleep = sleep,
    };

    public static ThrottleRuleOptions Remote(
        string name,
        int active,
        int total,
        TimeSpan sleep,
        string period = "minute") => new()
    {
        Name = name,
        Key = "remote_ip",
        Period = period,
        MaxActiveRequests = active,
        MaxTotalRequests = total,
        MaxSleep = sleep,
    };
}
