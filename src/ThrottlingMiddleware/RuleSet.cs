namespace ThrottlingMiddleware;

internal enum ThrottleKey
{
    Global,
    Endpoint,
    RemoteIp,
}

internal sealed class ThrottleRule
{
    public ThrottleRule(
        string name,
        ThrottleKey key,
        bool isDefault,
        string? method,
        string? path,
        int maxActiveRequests,
        int maxTotalRequests,
        TimeSpan maxSleep,
        int retryAfterSeconds)
    {
        Name = name;
        Key = key;
        IsDefault = isDefault;
        Method = method;
        Path = path;
        MaxActiveRequests = maxActiveRequests;
        MaxTotalRequests = maxTotalRequests;
        MaxSleep = maxSleep;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string Name { get; }

    public ThrottleKey Key { get; }

    public bool IsDefault { get; }

    public string? Method { get; }

    public string? Path { get; }

    public int MaxActiveRequests { get; }

    public int MaxTotalRequests { get; }

    public TimeSpan MaxSleep { get; }

    public int RetryAfterSeconds { get; }

    public bool Matches(string method, string path) =>
        string.Equals(Method, method, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);
}

internal sealed class RuleSet
{
    private RuleSet(ThrottleRule[] rules, int maxTrackedIps)
    {
        Rules = rules;
        MaxTrackedIps = maxTrackedIps;
    }

    public IReadOnlyList<ThrottleRule> Rules { get; }

    public int MaxTrackedIps { get; }

    public static RuleCompilation Create(ThrottlingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (options.MaxTrackedIps < 1)
            errors.Add("MaxTrackedIps must be at least 1.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<ThrottleRule>();
        var defaults = 0;

        foreach (var source in options.Rules)
        {
            var name = source.Name.Trim();
            if (name.Length == 0)
            {
                errors.Add("A rule name is empty.");
                continue;
            }

            if (!seen.Add(name))
                errors.Add($"Duplicate rule name '{name}'.");

            if (!TryParseKey(source.Key, out var key))
            {
                errors.Add($"Rule '{name}' has unknown key '{source.Key}'.");
                continue;
            }

            if (!TryParsePeriod(source.Period, out var retryAfter))
            {
                errors.Add($"Rule '{name}' has unknown period '{source.Period}'.");
                continue;
            }

            if (source.IsDefault)
            {
                defaults++;
                if (key != ThrottleKey.Global)
                    errors.Add($"Default rule '{name}' must use key 'global'.");
            }

            string? method = null;
            string? path = null;
            if (key == ThrottleKey.Endpoint && !TryParseEndpoint(source.Endpoint, out method, out path))
                errors.Add($"Endpoint rule '{name}' must set endpoint to 'METHOD /path'.");

            if (source.MaxActiveRequests < 1)
                errors.Add($"Rule '{name}' maxActiveRequests must be at least 1.");

            if (source.MaxTotalRequests < 1)
                errors.Add($"Rule '{name}' maxTotalRequests must be at least 1.");

            if (source.MaxTotalRequests < source.MaxActiveRequests)
                errors.Add($"Rule '{name}' maxTotalRequests is below maxActiveRequests.");

            if (source.MaxSleep < TimeSpan.Zero)
                errors.Add($"Rule '{name}' maxSleep must not be negative.");

            rules.Add(new ThrottleRule(
                name,
                key,
                source.IsDefault,
                method,
                path,
                source.MaxActiveRequests,
                source.MaxTotalRequests,
                source.MaxSleep,
                retryAfter));
        }

        if (defaults == 0)
            errors.Add("A default global rule is required.");
        else if (defaults > 1)
            errors.Add("Only one rule can be the default.");

        return errors.Count == 0
            ? new RuleCompilation(new RuleSet(rules.ToArray(), options.MaxTrackedIps), [])
            : new RuleCompilation(null, errors);
    }

    public static ThrottleRule Select(IReadOnlyList<ThrottleRule> rules, string method, string path)
    {
        ThrottleRule? endpoint = null;
        ThrottleRule? remote = null;
        ThrottleRule? global = null;
        ThrottleRule? fallback = null;

        foreach (var rule in rules)
        {
            switch (rule.Key)
            {
                case ThrottleKey.Endpoint when rule.Matches(method, path) && Tighter(rule, endpoint):
                    endpoint = rule;
                    break;
                case ThrottleKey.RemoteIp when Tighter(rule, remote):
                    remote = rule;
                    break;
                case ThrottleKey.Global when rule.IsDefault:
                    fallback = rule;
                    break;
                case ThrottleKey.Global when Tighter(rule, global):
                    global = rule;
                    break;
            }
        }

        return endpoint ?? remote ?? global ?? fallback
            ?? throw new InvalidOperationException("No throttling rule matched.");
    }

    private static bool Tighter(ThrottleRule candidate, ThrottleRule? current)
    {
        if (current is null)
            return true;

        var total = candidate.MaxTotalRequests.CompareTo(current.MaxTotalRequests);
        if (total != 0)
            return total < 0;

        var active = candidate.MaxActiveRequests.CompareTo(current.MaxActiveRequests);
        if (active != 0)
            return active < 0;

        var sleep = candidate.MaxSleep.CompareTo(current.MaxSleep);
        if (sleep != 0)
            return sleep < 0;

        return string.CompareOrdinal(candidate.Name, current.Name) < 0;
    }

    private static bool TryParseKey(string value, out ThrottleKey key)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "global":
                key = ThrottleKey.Global;
                return true;
            case "endpoint":
                key = ThrottleKey.Endpoint;
                return true;
            case "remote_ip":
                key = ThrottleKey.RemoteIp;
                return true;
            default:
                key = default;
                return false;
        }
    }

    private static bool TryParsePeriod(string value, out int retryAfterSeconds)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "minute":
                retryAfterSeconds = 60;
                return true;
            case "hour":
                retryAfterSeconds = 3600;
                return true;
            case "day":
                retryAfterSeconds = 86400;
                return true;
            default:
                retryAfterSeconds = 0;
                return false;
        }
    }

    private static bool TryParseEndpoint(string? value, out string method, out string path)
    {
        method = "";
        path = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !PathLooksValid(parts[1]))
            return false;

        method = parts[0];
        path = parts[1];
        return true;
    }

    private static bool PathLooksValid(string path) => path.StartsWith('/');
}

internal sealed class RuleCompilation
{
    public RuleCompilation(RuleSet? rules, IReadOnlyList<string> errors)
    {
        Rules = rules;
        Errors = errors;
    }

    public RuleSet? Rules { get; }

    public IReadOnlyList<string> Errors { get; }
}
