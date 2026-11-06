using System;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>超压联锁判定：上升沿、离线抑制、98% 解除、阀门 / 加热独立确认、重发间隔、复关、升级、迟到结果（纯逻辑）。</summary>
public class OverPressureGuardTests
{
    private const double Limit = 510;
    private static readonly DateTime T0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double sec) => T0.AddSeconds(sec);

    private static OverPressureActions Ev(OverPressureGuard g, double p, double sec, bool closed = false, bool? heatStopped = null,
        bool online = true, bool valves = true, bool heat = true, int ch = 0)
        => g.Evaluate(ch, p, online, Limit, At(sec), closed, valves, heatStopped, heat);

    [Fact]
    public void BelowLimit_NoAction()
    {
        var g = new OverPressureGuard();
        var a = Ev(g, 509.9, 0);
        Assert.False(a.Any);
        Assert.False(g.IsTripped(0));
    }

    [Fact]
    public void Offline_NoTrip_AndNaNIgnored()
    {
        var g = new OverPressureGuard();
        Assert.False(Ev(g, 600, 0, online: false).Any);
        Assert.False(Ev(g, double.NaN, 0).Any);
        Assert.False(g.IsTripped(0));
    }

    [Fact]
    public void RisingEdge_TripsAndSendsBothOnce()
    {
        var g = new OverPressureGuard();
        var a = Ev(g, 510, 0);   // ≥ 限值即触发
        Assert.True(a.Trip);
        Assert.True(a.SendValves);
        Assert.True(a.SendHeat);
        Assert.True(g.IsTripped(0));
        Assert.True(g.ValvesInFlight(0));
        Assert.True(g.HeatInFlight(0));
        // 在途：即使过了重发间隔也不再发
        var b = Ev(g, 530, 10);
        Assert.False(b.Trip);
        Assert.False(b.SendValves);
        Assert.False(b.SendHeat);
    }

    [Fact]
    public void HeatDisabled_NoHeatSend()
    {
        var g = new OverPressureGuard();
        var a = Ev(g, 520, 0, heat: false);
        Assert.True(a.Trip);
        Assert.True(a.SendValves);
        Assert.False(a.SendHeat);
        Assert.False(g.HeatInFlight(0));
        Assert.False(Ev(g, 520, 30, heat: false).SendHeat);
    }

    [Fact]
    public void ValvesNotConfigured_NoValveSend()
    {
        var g = new OverPressureGuard();
        var a = Ev(g, 520, 0, valves: false);
        Assert.True(a.Trip);
        Assert.False(a.SendValves);
        Assert.True(a.SendHeat);
    }

    [Fact]
    public void Rearm_OnlyBelow98Percent()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0);
        Assert.False(Ev(g, Limit * 0.99, 1).Cleared);   // 99%：仍触发
        Assert.True(g.IsTripped(0));
        Assert.False(Ev(g, Limit * 0.98, 2).Cleared);   // 恰好 98%：仍触发（需低于）
        var c = Ev(g, Limit * 0.979, 3);
        Assert.True(c.Cleared);
        Assert.False(g.IsTripped(0));
        // 重新布防：再次超压是新的上升沿
        Assert.True(Ev(g, 515, 4).Trip);
    }

    [Fact]
    public void ValvesAndHeat_ConfirmIndependently()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0);
        g.OnValveResult(0, true);
        // 阀门：回显成功 + 界面全关 → 确认；加热仍在途
        var a = Ev(g, 520, 1, closed: true);
        Assert.True(a.ValvesConfirmed);
        Assert.False(a.HeatConfirmed);
        Assert.True(g.IsValvesConfirmed(0));
        Assert.False(g.IsHeatConfirmed(0));
        // 加热：写入回显成功但回读 At≠4 → 不确认
        g.OnHeatResult(0, true);
        Assert.False(Ev(g, 520, 2, closed: true, heatStopped: false).HeatConfirmed);
        // 回读 At=4 → 确认
        Assert.True(Ev(g, 520, 3, closed: true, heatStopped: true).HeatConfirmed);
        Assert.True(g.IsHeatConfirmed(0));
        // 确认只出现一次
        var z = Ev(g, 520, 4, closed: true, heatStopped: true);
        Assert.False(z.ValvesConfirmed || z.HeatConfirmed || z.SendValves || z.SendHeat);
    }

    [Fact]
    public void Valves_NotConfirmedWithoutEcho_EvenIfShownClosed()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0);
        g.OnValveResult(0, false);   // 写入失败
        Assert.False(Ev(g, 520, 1, closed: true).ValvesConfirmed);   // 界面本来就全关也不算确认
        Assert.False(g.IsValvesConfirmed(0));
    }

    [Fact]
    public void HeatUnknownReadback_ConfirmsOnEcho()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0, heatStopped: null);
        g.OnHeatResult(0, true);
        Assert.True(Ev(g, 520, 1, heatStopped: null).HeatConfirmed);
    }

    [Fact]
    public void Retry_AtMostEvery5s_NotWhileInFlight()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0);
        g.OnValveResult(0, false);
        g.OnHeatResult(0, false);
        Assert.False(Ev(g, 520, 4.9).SendValves);   // 间隔未到
        var a = Ev(g, 520, 5.0);
        Assert.True(a.SendValves && a.ValvesRetry);
        Assert.True(a.SendHeat && a.HeatRetry);
        Assert.Equal(2, g.ValveSends(0));
        // 重发在途：不再发
        Assert.False(Ev(g, 520, 20).SendValves);
        g.OnValveResult(0, false);
        Assert.False(Ev(g, 520, 9.9).SendValves);   // 距上次下发 4.9 s
        Assert.True(Ev(g, 520, 10.1).SendValves);
    }

    [Fact]
    public void EscalationCounter_ReachesThree()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0, heat: false);
        Assert.Equal(1, g.ValveSends(0));
        g.OnValveResult(0, false);
        Ev(g, 520, 5, heat: false);
        g.OnValveResult(0, false);
        Ev(g, 520, 10, heat: false);
        Assert.Equal(OverTempGuard.EscalateAfterSends, g.ValveSends(0));
        // 确认后计数清零
        g.OnValveResult(0, true);
        Assert.True(Ev(g, 520, 11, closed: true, heat: false).ValvesConfirmed);
        Assert.Equal(0, g.ValveSends(0));
    }

    [Fact]
    public void Reopen_Valves_ShownOpenAfterConfirm()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0, heat: false);
        g.OnValveResult(0, true);
        Assert.True(Ev(g, 520, 1, closed: true, heat: false).ValvesConfirmed);
        // 序列 / 手动的一帧落在联锁之后：界面又显示打开
        var r = Ev(g, 520, 6, closed: false, heat: false);
        Assert.True(r.ValvesReopened);
        Assert.True(r.SendValves);
        Assert.False(g.IsValvesConfirmed(0));
        Assert.Equal(1, g.ValveSends(0));   // 复关重新计数
    }

    [Fact]
    public void Reopen_Heat_ReadbackNot4AfterConfirm()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0, valves: false);
        g.OnHeatResult(0, true);
        Assert.True(Ev(g, 520, 1, heatStopped: true, valves: false).HeatConfirmed);
        var r = Ev(g, 520, 2, heatStopped: false, valves: false);   // 距上次下发 < 5 s：先标记，不立即发
        Assert.False(r.SendHeat);
        var r2 = Ev(g, 520, 5.5, heatStopped: false, valves: false);
        Assert.True(r2.HeatReopened && r2.SendHeat);
    }

    [Fact]
    public void LateResultAfterClear_Ignored()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0);
        Assert.True(Ev(g, 100, 1).Cleared);
        g.OnValveResult(0, true);   // 迟到的回显
        g.OnHeatResult(0, true);
        Assert.False(g.IsTripped(0));
        // 重新触发时不能沿用迟到的回显直接确认
        Assert.True(Ev(g, 520, 2).Trip);
        Assert.False(Ev(g, 520, 3, closed: true, heatStopped: null).ValvesConfirmed);
    }

    [Fact]
    public void StaleResultAfterRetrip_IgnoredBySeq()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0);
        int oldV = g.ValveSeq(0), oldH = g.HeatSeq(0);
        Assert.True(Ev(g, 100, 1).Cleared);
        Assert.True(Ev(g, 520, 2).Trip);           // 很快重新触发：新的一帧在途
        int newV = g.ValveSeq(0);
        Assert.NotEqual(oldV, newV);
        // 上一次触发的那一帧这时才回来：不能清掉新一帧的在途标记，也不能算作确认
        g.OnValveResult(0, true, oldV);
        g.OnHeatResult(0, true, oldH);
        Assert.True(g.ValvesInFlight(0));
        Assert.True(g.HeatInFlight(0));
        // 新一帧失败：仍未确认，5 秒后照常重发
        g.OnValveResult(0, false, newV);
        Assert.False(Ev(g, 520, 3, closed: true).ValvesConfirmed);
        Assert.True(Ev(g, 520, 7.5, closed: true).SendValves);
    }

    [Fact]
    public void OfflineMidTrip_KeepsState()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0);
        g.OnValveResult(0, false);
        Assert.False(Ev(g, 0, 10, online: false).Any);   // 离线：读数是旧值，不解除也不重发
        Assert.True(g.IsTripped(0));
        Assert.True(Ev(g, 520, 11).SendValves);           // 恢复后照常重发
    }

    [Fact]
    public void Channels_AreIndependent()
    {
        var g = new OverPressureGuard();
        Assert.True(Ev(g, 520, 0, ch: 2).Trip);
        Assert.False(g.IsTripped(0));
        Assert.True(g.IsTripped(2));
    }

    [Fact]
    public void HeatEnabledMidTrip_StartsSending()
    {
        var g = new OverPressureGuard();
        Ev(g, 520, 0, heat: false);
        var a = Ev(g, 520, 1, heat: true);
        Assert.True(a.SendHeat && a.HeatRetry);
    }
}
