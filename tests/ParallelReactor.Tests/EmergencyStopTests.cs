using System;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

public class EmergencyStopTests
{
    private static Func<Task<bool>> Ok() => () => Task.FromResult(true);
    private static Func<Task<bool>> Fail() => () => Task.FromResult(false);
    private static Func<Task<bool>> Throws() => () => Task.FromException<bool>(new System.IO.IOException("bus down"));

    [Fact]
    public async Task AllSucceed_AllOk()
    {
        var r = await EmergencyStop.RunAsync(Ok(), Ok(), Ok());
        Assert.Equal(new EstopResult(EstopPart.Ok, EstopPart.Ok, EstopPart.Ok), r);
        Assert.True(r.AllConfirmed);
    }

    [Fact]
    public async Task OnePartThrows_OthersStillOk()
    {
        var r = await EmergencyStop.RunAsync(Throws(), Ok(), Ok());
        Assert.Equal(EstopPart.Failed, r.Heat);
        Assert.Equal(EstopPart.Ok, r.Stir);
        Assert.Equal(EstopPart.Ok, r.Valves);
        Assert.False(r.AllConfirmed);
    }

    [Fact]
    public async Task FalseResult_IsFailed()
    {
        var r = await EmergencyStop.RunAsync(Ok(), Fail(), Ok());
        Assert.Equal(EstopPart.Failed, r.Stir);
        Assert.False(r.AllConfirmed);
    }

    [Fact]
    public async Task SynchronousThrow_IsFailed()
    {
        // op 在返回 Task 之前就同步抛出，也只能算该路失败
        Func<Task<bool>> boom = () => throw new InvalidOperationException("sync");
        var r = await EmergencyStop.RunAsync(Ok(), Ok(), boom);
        Assert.Equal(EstopPart.Failed, r.Valves);
        Assert.Equal(EstopPart.Ok, r.Heat);
    }

    [Fact]
    public async Task NullPart_IsNotConfigured_AndNotAFailure()
    {
        var r = await EmergencyStop.RunAsync(null, Ok(), null);
        Assert.Equal(EstopPart.NotConfigured, r.Heat);
        Assert.Equal(EstopPart.NotConfigured, r.Valves);
        Assert.True(r.AllConfirmed);
    }

    [Fact]
    public async Task Parts_RunInParallel_SlowBusDoesNotDelayOthers()
    {
        // 加热那路卡住：搅拌和阀门必须在它返回之前就已经下发
        var heatGate = new TaskCompletionSource<bool>();
        int started = 0;
        Func<Task<bool>> slow = () => { Interlocked.Increment(ref started); return heatGate.Task; };
        Func<Task<bool>> fast = () => { Interlocked.Increment(ref started); return Task.FromResult(true); };

        var run = EmergencyStop.RunAsync(slow, fast, fast);
        Assert.Equal(3, Volatile.Read(ref started));   // 三路都已发出
        Assert.False(run.IsCompleted);                  // 整体等最慢的那路

        heatGate.SetResult(true);
        var r = await run;
        Assert.True(r.AllConfirmed);
    }

    [Fact]
    public async Task PartAsync_Mapping()
    {
        Assert.Equal(EstopPart.Ok, await EmergencyStop.PartAsync(Ok()));
        Assert.Equal(EstopPart.Failed, await EmergencyStop.PartAsync(Fail()));
        Assert.Equal(EstopPart.Failed, await EmergencyStop.PartAsync(Throws()));
        Assert.Equal(EstopPart.NotConfigured, await EmergencyStop.PartAsync(null));
    }

    [Fact]
    public void PendingIsNotConfirmedFailure()
    {
        // 下发中不算失败（AllConfirmed 只看 Failed）；面板另行显示「下发中」
        Assert.True(EstopResult.AllPending.AllConfirmed);
    }
}
