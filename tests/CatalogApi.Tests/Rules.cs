using ThrottlingMiddleware;

namespace CatalogApi.Tests;

internal static class Rules
{
    public static ThrottleRuleOptions Global(
        string name,
        int active,
        int total,
        TimeSpan sleep,
        bool isDefault = false,
        string period = "minute") => new()
    {
        Name = name,
        Key = "global",
        Period = period,
        IsDefault = isDefault,
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
        Period = period,
        Endpoint = endpoint,
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
