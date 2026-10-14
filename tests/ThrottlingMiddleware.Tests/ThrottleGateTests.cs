using System.Diagnostics;
using ThrottlingMiddleware;

namespace ThrottlingMiddleware.Tests;

public class ThrottleGateTests
{
    [Fact]
    public async Task First_acquire_succeeds_and_a_full_total_gate_does_not_wait()
    {
        var gate = new ThrottleGate(1, 1, TimeSpan.FromSeconds(5));

        Assert.Equal(GateDecision.Acquired, await gate.AcquireAsync(CancellationToken.None));

        var started = Stopwatch.StartNew();
        var decision = await gate.AcquireAsync(CancellationToken.None);
        started.Stop();

        Assert.Equal(GateDecision.RejectedTotal, decision);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Zero_sleep_rejects_without_waiting_and_frees_the_total_slot()
    {
        var gate = new ThrottleGate(1, 2, TimeSpan.Zero);
        Assert.Equal(GateDecision.Acquired, await gate.AcquireAsync(CancellationToken.None));

        var started = Stopwatch.StartNew();
        var decision = await gate.AcquireAsync(CancellationToken.None);
        started.Stop();

        Assert.Equal(GateDecision.RejectedSleep, decision);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(200));

        var third = await gate.AcquireAsync(CancellationToken.None);
        Assert.Equal(GateDecision.RejectedSleep, third);
    }

    [Fact]
    public async Task Sleeper_runs_after_the_holder_releases()
    {
        var gate = new ThrottleGate(1, 2, TimeSpan.FromSeconds(2));
        Assert.Equal(GateDecision.Acquired, await gate.AcquireAsync(CancellationToken.None));

        var waiting = gate.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        gate.Release();

        var completed = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(waiting, completed);
        Assert.Equal(GateDecision.Acquired, await waiting);
        gate.Release();
    }

    [Fact]
    public async Task Sleep_timeout_frees_the_total_slot()
    {
        var gate = new ThrottleGate(1, 2, TimeSpan.FromMilliseconds(200));
        Assert.Equal(GateDecision.Acquired, await gate.AcquireAsync(CancellationToken.None));

        var started = Stopwatch.StartNew();
        var decision = await gate.AcquireAsync(CancellationToken.None);
        started.Stop();

        Assert.Equal(GateDecision.RejectedSleep, decision);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(150));

        var third = await gate.AcquireAsync(CancellationToken.None);
        Assert.Equal(GateDecision.RejectedSleep, third);
    }

    [Fact]
    public async Task Cancel_throws_and_frees_the_total_slot()
    {
        var gate = new ThrottleGate(1, 2, TimeSpan.FromSeconds(5));
        Assert.Equal(GateDecision.Acquired, await gate.AcquireAsync(CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(cancellation.Token);
        await Task.Delay(50);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var third = await gate.AcquireAsync(CancellationToken.None);
        Assert.Equal(GateDecision.RejectedSleep, third);
    }
}
