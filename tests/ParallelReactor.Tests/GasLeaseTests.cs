using System.Collections.Generic;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C8 / C9：气路按占用者中止 / 释放；参与通道被停止 / 停用 / 报警时序列中止。</summary>
public class GasLeaseTests
{
    private static Dictionary<int, ReactorState> St(params (int Id, ReactorState S)[] xs)
    {
        var d = new Dictionary<int, ReactorState>();
        foreach (var (id, s) in xs) d[id] = s;
        return d;
    }

    [Fact]
    public void OnlyOwnerCanAbort()
    {
        var l = new GasLease();
        Assert.False(l.CanAbort(GasOwner.Leak));   // 空闲
        Assert.False(l.CanAbort(GasOwner.None));
        l.Begin(GasOwner.Program, "RV1 程序 · 升压", new[] { 1 }, new[] { 1 }, St((1, ReactorState.Pressing)));
        Assert.False(l.CanAbort(GasOwner.Leak));   // 泄漏测试面板的「中止」不能停程序的升压
        Assert.True(l.CanAbort(GasOwner.Program));
        Assert.False(l.CanAbort(GasOwner.None));

        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 2, 3 }, new[] { 2, 3 }, St((2, ReactorState.Idle), (3, ReactorState.Done)));
        Assert.False(l.CanAbort(GasOwner.Program));   // 程序中止不能停泄漏测试
        Assert.True(l.CanAbort(GasOwner.Leak));
    }

    [Fact]
    public void TryEnd_WrongOwnerIsNoOp()
    {
        var l = new GasLease();
        long g = l.Begin(GasOwner.Leak, "泄漏测试", new[] { 2 }, new[] { 2 }, St((2, ReactorState.Idle)));
        Assert.False(l.TryEnd(GasOwner.Program));
        Assert.True(l.Busy);
        Assert.Equal("泄漏测试", l.Task);
        Assert.Equal(new[] { 2 }, l.Watch);
        Assert.False(l.TryEnd(GasOwner.Leak, g + 1));   // 代号不符
        Assert.True(l.TryEnd(GasOwner.Leak, g));
        Assert.False(l.Busy);
        Assert.Empty(l.Watch);
        Assert.False(l.TryEnd(GasOwner.Leak));
    }

    [Fact]
    public void SameOwnerRenewal_EndsWithOwner()
    {
        var l = new GasLease();
        long g1 = l.Begin(GasOwner.Program, "升压", new[] { 1 }, new[] { 1 }, null);
        long g2 = l.Begin(GasOwner.Program, "稳压", new[] { 1 }, new[] { 1 }, null);
        Assert.NotEqual(g1, g2);
        Assert.False(l.TryEnd(GasOwner.Program, g1));   // 旧代号不能释放新租约
        Assert.True(l.TryEnd(GasOwner.Program));
    }

    [Fact]
    public void IncludesAndInvalidate()
    {
        var l = new GasLease();
        Assert.False(l.Includes(1));
        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 1, 2 }, new[] { 1, 2 }, St((1, ReactorState.Done), (2, ReactorState.Done)));
        Assert.True(l.Includes(2));
        Assert.False(l.Includes(5));
        Assert.Null(l.FaultReason(_ => ReactorState.Done));
        l.Invalidate(5);   // 不参与：忽略
        Assert.Null(l.FaultReason(_ => ReactorState.Done));
        l.Invalidate(2);   // 已结束通道再按「停止」：状态不变，靠失效标记
        Assert.True(l.IsInvalidated(2));
        Assert.Contains("RV2", l.FaultReason(_ => ReactorState.Done));
        // 同一占用者续租保留失效标记；释放后的新租约从干净状态开始
        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 2 }, null, St((2, ReactorState.Done)));
        Assert.Contains("RV2", l.FaultReason(_ => ReactorState.Done));
        Assert.True(l.TryEnd(GasOwner.Leak));
        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 2 }, null, St((2, ReactorState.Done)));
        Assert.Null(l.FaultReason(_ => ReactorState.Done));
    }

    [Fact]
    public void TransitionIntoDoneOrIdle_Faults()
    {
        var l = new GasLease();
        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 1, 2 }, null, St((1, ReactorState.Ready), (2, ReactorState.Idle)));
        Assert.Null(l.FaultReason(id => id == 1 ? ReactorState.Ready : ReactorState.Idle));
        Assert.Contains("RV1 已被停止", l.FaultReason(id => id == 1 ? ReactorState.Done : ReactorState.Idle));
        Assert.Contains("RV1 已被停用", l.FaultReason(id => id == 1 ? ReactorState.Idle : ReactorState.Idle));
        Assert.Contains("RV2 已被停止", l.FaultReason(id => id == 1 ? ReactorState.Ready : ReactorState.Done));   // Idle → Done
    }

    [Fact]
    public void ProgramStateWalk_DoesNotFault_AlarmDoes()
    {
        var l = new GasLease();
        l.Begin(GasOwner.Program, "RV1 程序 · 稳压", new[] { 1 }, new[] { 1 }, St((1, ReactorState.Pressing)));
        foreach (var s in new[] { ReactorState.Pressing, ReactorState.Wait, ReactorState.React, ReactorState.Stir, ReactorState.Heating, ReactorState.Cooling })
            Assert.Null(l.FaultReason(_ => s));
        Assert.Contains("报警", l.FaultReason(_ => ReactorState.Alarm));
        Assert.NotNull(l.FaultReason(_ => ReactorState.Done));
        Assert.Null(l.FaultReason(_ => null));   // 未知通道不判
    }

    [Fact]
    public void Idle_NoFault()
    {
        var l = new GasLease();
        Assert.Null(l.FaultReason(_ => ReactorState.Alarm));
        l.Begin(GasOwner.Program, "吹扫", null, null, null);
        Assert.Null(l.FaultReason(_ => ReactorState.Alarm));   // 无参与通道
        l.Clear();
        Assert.False(l.Busy);
    }

    [Fact]
    public void Renew_SameOwner_KeepsInvalidationAndStartState()
    {
        // 升压期间 RV1 被抽屉停止，随后升压 → 稳压续租：失效标记不能丢，也不能把「已结束」记成开始状态
        var l = new GasLease();
        l.Begin(GasOwner.Program, "升压", new[] { 1 }, new[] { 1 }, St((1, ReactorState.Pressing)));
        l.Invalidate(1);
        l.Begin(GasOwner.Program, "稳压", new[] { 1 }, new[] { 1 }, St((1, ReactorState.Done)));
        Assert.True(l.IsInvalidated(1));
        Assert.NotNull(l.FaultReason(_ => ReactorState.Done));

        // 没被标记失效、只是续租时状态已转为已结束：开始状态沿用 Pressing，仍判为被停止
        var m = new GasLease();
        m.Begin(GasOwner.Program, "升压", new[] { 1 }, new[] { 1 }, St((1, ReactorState.Pressing)));
        m.Begin(GasOwner.Program, "稳压", new[] { 1 }, new[] { 1 }, St((1, ReactorState.Done)));
        Assert.NotNull(m.FaultReason(_ => ReactorState.Done));
    }

    [Fact]
    public void NewOwner_ClearsInvalidation_RenewDropsNonParticipants()
    {
        var l = new GasLease();
        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 1, 2 }, new[] { 1, 2 }, St((1, ReactorState.Done), (2, ReactorState.Done)));
        l.Invalidate(1);
        l.Invalidate(2);
        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 2 }, new[] { 2 }, St((2, ReactorState.Done)));
        Assert.False(l.IsInvalidated(1));   // 不再参与的通道不保留
        Assert.True(l.IsInvalidated(2));
        Assert.True(l.TryEnd(GasOwner.Leak));
        l.Begin(GasOwner.Program, "升压", new[] { 2 }, new[] { 2 }, St((2, ReactorState.Pressing)));
        Assert.False(l.IsInvalidated(2));   // 新占用者从干净状态开始
        Assert.Null(l.FaultReason(_ => ReactorState.Pressing));
    }
}
