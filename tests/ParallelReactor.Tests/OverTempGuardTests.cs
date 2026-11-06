using System;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>超温切断判定：上升沿、重发间隔、离线抑制、回差解除、回读复切（纯逻辑）。
/// 判定不接收通道状态参数——停用 / 已结束 / 自整定中的通道与运行中通道走同一逻辑（MainViewModel.Tick 对所有通道调用）。</summary>
public class OverTempGuardTests
{
    private const double Limit = 250;
    private static readonly DateTime T0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double sec) => T0.AddSeconds(sec);

    [Fact]
    public void BelowLimit_NoAction()
    {
        var g = new OverTempGuard();
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 249.9, true, Limit, T0));
        Assert.False(g.IsTripped(0));
    }

    [Fact]
    public void RisingEdge_TripsOnce_ThenNoSpamWhileInFlight()
    {
        var g = new OverTempGuard();
        Assert.Equal(OverTempAction.TripSend, g.Evaluate(0, 250, true, Limit, At(0)));   // ≥ 限值即触发
        Assert.True(g.IsTripped(0));
        Assert.True(g.InFlight(0));
        // 写入还没回来：即使过了重发间隔也不再发
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 260, true, Limit, At(1.2)));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 260, true, Limit, At(10)));
    }

    [Fact]
    public void Unconfirmed_RetriesEvery5s()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 255, true, Limit, At(0));
        g.OnStopResult(0, false, At(0.3));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, true, Limit, At(1.2)));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, true, Limit, At(4.8)));
        Assert.Equal(OverTempAction.RetrySend, g.Evaluate(0, 255, true, Limit, At(5.0)));
        g.OnStopResult(0, false, At(5.3));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, true, Limit, At(8)));
        Assert.Equal(OverTempAction.RetrySend, g.Evaluate(0, 255, true, Limit, At(10.1)));
    }

    [Fact]
    public void WriteAck_IsNotConfirmation_ReadbackAt4Is()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 255, true, Limit, At(0), outputStopped: false);
        g.OnStopResult(0, true, At(0.2));
        Assert.False(g.IsConfirmed(0));   // 只是写入回显
        // 写入之前就开始的轮询带回的旧 At（≠4）：不确认，也不立刻重发
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, true, Limit, At(1.2), outputStopped: false));
        Assert.Equal(OverTempAction.Confirmed, g.Evaluate(0, 255, true, Limit, At(2.4), outputStopped: true));
        Assert.True(g.IsConfirmed(0));
        for (int i = 3; i < 30; i++)   // 「已确认」只出现一次，之后不再发
            Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, true, Limit, At(i * 1.2), outputStopped: true));
    }

    [Fact]
    public void AckedButReadbackNeverAt4_KeepsRetrying_AndCountsForEscalation()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 255, true, Limit, At(0), outputStopped: false);
        for (int k = 1; k < OverTempGuard.EscalateAfterSends; k++)
        {
            g.OnStopResult(0, true, At(5 * (k - 1) + 0.2));   // 仪表回显正常，但 At 始终不是 4
            Assert.Equal(OverTempAction.RetrySend, g.Evaluate(0, 255, true, Limit, At(5 * k), outputStopped: false));
        }
        Assert.Equal(OverTempGuard.EscalateAfterSends, g.UnconfirmedSends(0));
        Assert.False(g.IsConfirmed(0));
    }

    [Fact]
    public void UnknownMode_FallsBackToWriteAck()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 255, true, Limit, At(0));
        g.OnStopResult(0, true, At(0.2));
        Assert.Equal(OverTempAction.Confirmed, g.Evaluate(0, 255, true, Limit, At(1.2)));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, true, Limit, At(30)));
    }

    [Fact]
    public void Confirmed_ThenReadbackShowsOutputRunning_Reopens()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 255, true, Limit, At(0));
        g.OnStopResult(0, true, At(0.2));
        Assert.Equal(OverTempAction.Confirmed, g.Evaluate(0, 255, true, Limit, At(1.2), outputStopped: true));
        // 有人重新启动了加热（At=0）：距上次下发已超过重发间隔 → 立即复切，并单独报出
        Assert.Equal(OverTempAction.Reopened, g.Evaluate(0, 255, true, Limit, At(20), outputStopped: false));
        Assert.False(g.IsConfirmed(0));
        Assert.True(g.InFlight(0));
        g.OnStopResult(0, true, At(20.2));
        Assert.Equal(OverTempAction.Confirmed, g.Evaluate(0, 255, true, Limit, At(21.4), outputStopped: true));
    }

    [Fact]
    public void Reopened_WithinRetryInterval_WaitsThenReopens()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 255, true, Limit, At(0));
        g.OnStopResult(0, true, At(0.2));
        g.Evaluate(0, 255, true, Limit, At(1.2), outputStopped: true);
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, true, Limit, At(2.4), outputStopped: false));   // 不刷总线
        Assert.Equal(OverTempAction.Reopened, g.Evaluate(0, 255, true, Limit, At(5.0), outputStopped: false));
    }

    [Fact]
    public void FailedCut_KeepsRetryingInHysteresisBand()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 251, true, Limit, At(0));
        g.OnStopResult(0, false, At(0.3));
        // PV 落到 限值 − 回差 与 限值 之间：输出状态未知，仍要重发
        Assert.Equal(OverTempAction.RetrySend, g.Evaluate(0, Limit - 1, true, Limit, At(5), outputStopped: false));
    }

    [Fact]
    public void Offline_NeverTrips_AndDoesNotRetryOrClear()
    {
        var g = new OverTempGuard();
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 999, false, Limit, At(0)));   // PV 是旧值：不触发
        Assert.False(g.IsTripped(0));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, double.NaN, true, Limit, At(1)));

        g.Evaluate(0, 255, true, Limit, At(2));
        g.OnStopResult(0, false, At(2.2));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 255, false, Limit, At(20)));  // 离线不重发
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 20, false, Limit, At(21)));   // 离线也不解除
        Assert.True(g.IsTripped(0));
        Assert.Equal(OverTempAction.RetrySend, g.Evaluate(0, 255, true, Limit, At(22)));   // 恢复在线后继续重发
    }

    [Fact]
    public void Clears_OnlyBelowHysteresis_ThenRearms()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 251, true, Limit, At(0));
        g.OnStopResult(0, true, At(0.1));
        Assert.Equal(OverTempAction.Confirmed, g.Evaluate(0, Limit - OverTempGuard.Hysteresis + 0.1, true, Limit, At(1), outputStopped: true));
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 249.9, true, Limit, At(30), outputStopped: true));   // 回差区内且已确认：不重发、不解除
        Assert.Equal(OverTempAction.Cleared, g.Evaluate(0, Limit - OverTempGuard.Hysteresis - 0.1, true, Limit, At(40)));
        Assert.False(g.IsTripped(0));
        Assert.Equal(OverTempAction.TripSend, g.Evaluate(0, 250.5, true, Limit, At(50)));   // 重新布防后再次上升沿
    }

    [Fact]
    public void LateResultAfterClear_IsIgnored()
    {
        var g = new OverTempGuard();
        g.Evaluate(0, 251, true, Limit, At(0));
        g.Evaluate(0, 200, true, Limit, At(1));   // 结果回来前已降温解除
        g.OnStopResult(0, true, At(2));
        Assert.False(g.IsTripped(0));
        Assert.False(g.IsConfirmed(0));
    }

    [Fact]
    public void Channels_AreIndependent()
    {
        var g = new OverTempGuard(8);
        Assert.Equal(OverTempAction.TripSend, g.Evaluate(6, 300, true, Limit, At(0)));
        Assert.Equal(OverTempAction.None, g.Evaluate(5, 100, true, Limit, At(0)));
        Assert.Equal(OverTempAction.TripSend, g.Evaluate(5, 300, true, Limit, At(1)));
        Assert.True(g.IsTripped(5) && g.IsTripped(6) && !g.IsTripped(0));
    }

    [Fact]
    public void LimitLoweredWhileHot_TripsImmediately()
    {
        var g = new OverTempGuard();
        Assert.Equal(OverTempAction.None, g.Evaluate(0, 80, true, 250, At(0)));
        Assert.Equal(OverTempAction.TripSend, g.Evaluate(0, 80, true, 60, At(1.2)));   // 设置页把限值改低
    }
}
