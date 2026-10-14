using System.Collections.Concurrent;
using System.Net;

namespace ThrottlingMiddleware;

internal sealed class GateRegistry
{
    private readonly Dictionary<ThrottleRule, ThrottleGate> _fixed = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<IPAddress, Lazy<ThrottleGate>> _addresses = new();
    private readonly Dictionary<ThrottleRule, ThrottleGate> _nullAddresses = new(ReferenceEqualityComparer.Instance);
    private readonly int _maxTracked;
    private readonly object _create = new();

    public GateRegistry(RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _maxTracked = rules.MaxTrackedIps;
        foreach (var rule in rules.Rules)
        {
            if (rule.Key != ThrottleKey.RemoteIp)
                _fixed.Add(rule, Create(rule));
        }
    }

    public ThrottleGate? Get(ThrottleRule rule, IPAddress? remoteIp)
    {
        if (rule.Key != ThrottleKey.RemoteIp)
            return _fixed[rule];

        if (remoteIp is null)
            return NullAddress(rule);

        if (_addresses.TryGetValue(remoteIp, out var known))
            return known.Value;

        lock (_create)
        {
            if (_addresses.TryGetValue(remoteIp, out known))
                return known.Value;

            if (_addresses.Count >= _maxTracked && !DropIdle())
                return null;

            var created = new Lazy<ThrottleGate>(() => Create(rule), LazyThreadSafetyMode.ExecutionAndPublication);
            var stored = _addresses.GetOrAdd(remoteIp, created);
            return stored.Value;
        }
    }

    private ThrottleGate NullAddress(ThrottleRule rule)
    {
        lock (_create)
        {
            if (_nullAddresses.TryGetValue(rule, out var gate))
                return gate;

            gate = Create(rule);
            _nullAddresses.Add(rule, gate);
            return gate;
        }
    }

    private bool DropIdle()
    {
        foreach (var entry in _addresses)
        {
            var lazy = entry.Value;
            if (!lazy.IsValueCreated || lazy.Value.IsIdle)
                _addresses.TryRemove(new KeyValuePair<IPAddress, Lazy<ThrottleGate>>(entry.Key, lazy));
        }

        return _addresses.Count < _maxTracked;
    }

    private static ThrottleGate Create(ThrottleRule rule) =>
        new(rule.MaxActiveRequests, rule.MaxTotalRequests, rule.MaxSleep);
}
