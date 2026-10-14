using Microsoft.Extensions.Options;

namespace ThrottlingMiddleware;

public sealed class ThrottlingOptions
{
    public int MaxTrackedIps { get; set; } = 10_000;

    public List<ThrottleRuleOptions> Rules { get; set; } = [];
}

public sealed class ThrottleRuleOptions
{
    public string Name { get; set; } = "";

    public string Key { get; set; } = "";

    public string Period { get; set; } = "";

    public bool IsDefault { get; set; }

    public string? Endpoint { get; set; }

    public int MaxActiveRequests { get; set; }

    public int MaxTotalRequests { get; set; }

    public TimeSpan MaxSleep { get; set; }
}

internal sealed class ThrottlingOptionsValidator : IValidateOptions<ThrottlingOptions>
{
    public ValidateOptionsResult Validate(string? name, ThrottlingOptions options)
    {
        var created = RuleSet.Create(options);
        return created.Errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(created.Errors);
    }
}
