namespace ThrottlingMiddleware;

internal enum GateDecision
{
    Acquired,
    RejectedTotal,
    RejectedSleep,
}

internal sealed class ThrottleGate
{
    private readonly SemaphoreSlim _total;
    private readonly SemaphoreSlim _active;
    private readonly TimeSpan _maxSleep;
    private readonly int _maxTotal;
    private readonly int _maxActive;

    public ThrottleGate(int maxActive, int maxTotal, TimeSpan maxSleep)
    {
        _maxActive = maxActive;
        _maxTotal = maxTotal;
        _maxSleep = maxSleep;
        _total = new SemaphoreSlim(maxTotal, maxTotal);
        _active = new SemaphoreSlim(maxActive, maxActive);
    }

    public bool IsIdle => _total.CurrentCount == _maxTotal && _active.CurrentCount == _maxActive;

    public async Task<GateDecision> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!_total.Wait(0))
            return GateDecision.RejectedTotal;

        try
        {
            var active = _active.Wait(0);
            if (!active && _maxSleep > TimeSpan.Zero)
                active = await _active.WaitAsync(_maxSleep, cancellationToken).ConfigureAwait(false);

            if (!active)
            {
                _total.Release();
                return GateDecision.RejectedSleep;
            }

            return GateDecision.Acquired;
        }
        catch
        {
            _total.Release();
            throw;
        }
    }

    public void Release()
    {
        _active.Release();
        _total.Release();
    }
}
