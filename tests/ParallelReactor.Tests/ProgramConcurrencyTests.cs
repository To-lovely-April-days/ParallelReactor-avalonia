using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>多程序并行：运行集合小件（IsOwned / FirstRunning / Summary）。</summary>
public class ProgramRunSetTests
{
    private static Dictionary<int, ProgramRunInfo> Runs(params (int Rv, ProgramRunPhase P)[] xs)
    {
        var d = new Dictionary<int, ProgramRunInfo>();
        foreach (var (rv, p) in xs) d[rv] = new ProgramRunInfo(rv, 3, "local", DateTimeOffset.UnixEpoch) { Phase = p };
        return d;
    }

    [Fact]
    public void Empty()
    {
        var r = Runs();
        Assert.False(ProgramRunSet.IsOwned(r, 1));
        Assert.Null(ProgramRunSet.FirstRunning(r));
        Assert.Equal("", ProgramRunSet.Summary(r));
        Assert.Empty(ProgramRunSet.RunningIds(r));
    }

    [Fact]
    public void SingleAndMultiple()
    {
        var r = Runs((3, ProgramRunPhase.Running), (1, ProgramRunPhase.WaitingGas), (5, ProgramRunPhase.Completed));
        Assert.True(ProgramRunSet.IsOwned(r, 1));
        Assert.True(ProgramRunSet.IsOwned(r, 3));
        Assert.False(ProgramRunSet.IsOwned(r, 5));   // 已结束不算占用
        Assert.False(ProgramRunSet.IsOwned(r, 2));
        Assert.Equal(1, ProgramRunSet.FirstRunning(r)!.Rv);   // 取通道号最小
        Assert.Equal("正在运行：RV1 RV3", ProgramRunSet.Summary(r));
        Assert.Equal(new[] { 1, 3 }, ProgramRunSet.RunningIds(r));
    }

    [Fact]
    public void PhaseNames_AreCamelCase()
    {
        Assert.Equal("waitingGas", ProgramRunSet.PhaseName(ProgramRunPhase.WaitingGas));
        Assert.Equal("running", ProgramRunSet.PhaseName(ProgramRunPhase.Running));
        Assert.True(new ProgramRunInfo(1, 1, "local", DateTimeOffset.UnixEpoch) { Phase = ProgramRunPhase.Finishing }.IsActive);
        Assert.False(new ProgramRunInfo(1, 1, "local", DateTimeOffset.UnixEpoch) { Phase = ProgramRunPhase.Aborted }.IsActive);
        Assert.True(new ProgramRunInfo(1, 1, "local", DateTimeOffset.UnixEpoch) { Phase = ProgramRunPhase.WaitingGas }.WaitingGas);
    }
}

/// <summary>多程序并行：气路等待队列先到先得 / 超时 / 重复入队。</summary>
public class GasQueueTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromHours(8);   // 单调时间（开机起流逝量）

    [Fact]
    public void Fifo_HeadAdvancesOnRemove()
    {
        var q = new GasQueue();
        long a = q.Enqueue(1, T0), b = q.Enqueue(3, T0.Add(TimeSpan.FromSeconds(1))), c = q.Enqueue(2, T0.Add(TimeSpan.FromSeconds(2)));
        Assert.True(q.IsHead(a));
        Assert.False(q.IsHead(b));
        Assert.Equal(new[] { 1, 3, 2 }, q.Waiting);
        Assert.True(q.Remove(a));
        Assert.True(q.IsHead(b));
        Assert.False(q.IsHead(c));
        Assert.Equal(2, q.Count);
    }

    [Fact]
    public void RemoveMiddle_KeepsOrder()
    {
        var q = new GasQueue();
        long a = q.Enqueue(1, T0), b = q.Enqueue(2, T0), c = q.Enqueue(3, T0);
        Assert.True(q.Remove(b));
        Assert.False(q.Remove(b));   // 已出队
        Assert.True(q.IsHead(a));
        q.Remove(a);
        Assert.True(q.IsHead(c));
        Assert.Equal(new[] { 3 }, q.Waiting);
    }

    [Fact]
    public void DuplicateEnqueue_ReturnsSameTicket_KeepsSince()
    {
        var q = new GasQueue();
        long a = q.Enqueue(4, T0);
        long a2 = q.Enqueue(4, T0.Add(TimeSpan.FromMinutes(5)));
        Assert.Equal(a, a2);
        Assert.Equal(1, q.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), q.Waited(a, T0.Add(TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void Timeout_Boundary60Min()
    {
        var q = new GasQueue();
        long a = q.Enqueue(1, T0);
        var max = TimeSpan.FromMinutes(60);
        Assert.False(q.TimedOut(a, T0.Add(TimeSpan.FromMinutes(59.99)), max));
        Assert.True(q.TimedOut(a, T0.Add(TimeSpan.FromMinutes(60)), max));
        Assert.False(q.TimedOut(a, T0.Add(TimeSpan.FromHours(5)), TimeSpan.Zero));   // 0 = 不限
        Assert.False(q.TimedOut(999, T0.Add(TimeSpan.FromHours(5)), max));   // 不在队中
        Assert.Equal(TimeSpan.Zero, q.Waited(999, T0));
    }
}

/// <summary>多程序并行：搅拌引用计数（最后一个释放才停；改写他人转速时返回被改写者）。</summary>
public class StirShareTests
{
    [Fact]
    public void Request_ReturnsOverriddenHolder_OnlyWhenRpmDiffers()
    {
        var s = new StirShare();
        Assert.Null(s.Request(1, 1000));            // 第一个
        Assert.Equal((1, 1000), s.Request(2, 600)); // 改写 RV1 的 1000
        Assert.Null(s.Request(3, 600));             // 同转速：不算改写
        Assert.Equal(3, s.Count);
        Assert.Equal(600, s.LastRpm);
        Assert.Equal((3, 600), s.Request(2, 800));  // 全局转速最近由 RV3 设定：RV2 改写的是 RV3 的
        Assert.Null(s.Request(2, 900));             // 自己改自己刚设的
    }

    [Fact]
    public void Release_LastOneStops()
    {
        var s = new StirShare();
        s.Request(1, 1000);
        s.Request(2, 600);
        Assert.True(s.Release(1));    // RV2 仍持有
        Assert.True(s.Holds(2));
        Assert.False(s.Holds(1));
        Assert.False(s.Release(2));   // 最后一个
        Assert.Equal(0, s.Count);
        Assert.False(s.Release(7));   // 未登记
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var s = new StirShare();
        s.Request(1, 500);
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.Null(s.LastRpm);
        Assert.Null(s.Request(2, 600));   // 清空后没有可改写的对象
    }

    [Fact]
    public void SingleProgram_BehavesLikeToday()
    {
        var s = new StirShare();
        Assert.Null(s.Request(1, 1000));
        Assert.False(s.Release(1));   // 单程序：步骤结束即停搅拌
    }

    /// <summary>审查项：释放最近设定者后，「最近设定」回退为剩余持有者（含其转速），warn 事件不能张冠李戴，物理转速应恢复。</summary>
    [Fact]
    public void Release_LastSetter_RestoresPreviousHolderRpm()
    {
        var s = new StirShare();
        s.Request(1, 600);
        Assert.Equal((1, 600), s.Request(2, 900));   // RV2 改写 RV1
        Assert.True(s.Release(2));                    // RV2 先结束
        Assert.Equal((1, 600), s.Current);            // 剩余持有者 RV1 要求 600：调用方据此恢复全局转速
        Assert.Equal(600, s.LastRpm);
        Assert.Null(s.Request(3, 600));               // 同转速：不是改写
        Assert.Equal((3, 600), s.Request(4, 800));    // 改写的是最近登记者 RV3，而不是早已释放的 RV2
    }

    [Fact]
    public void Release_NonLastSetter_KeepsLastRpm()
    {
        var s = new StirShare();
        s.Request(1, 600);
        s.Request(2, 900);
        Assert.True(s.Release(1));
        Assert.Equal(900, s.LastRpm);
        Assert.Equal((2, 900), s.Current);
    }

    [Fact]
    public void Request_MovesToEnd_SoCurrentIsMostRecent()
    {
        var s = new StirShare();
        s.Request(1, 600);
        s.Request(2, 900);
        s.Request(1, 700);                 // RV1 再登记：成为最近登记者
        Assert.Equal((1, 700), s.Current);
        Assert.Equal(new[] { 2, 1 }, s.Holders);
    }
}

/// <summary>多程序并行：租约按通道隔离、气路守卫在程序排队模式下的放宽。</summary>
public class GasLeasePerChannelTests
{
    [Fact]
    public void OwnerRv_RecordedAndCleared_AbortPerChannel()
    {
        var l = new GasLease();
        l.Begin(GasOwner.Program, "RV1 程序 · 升压", new[] { 1 }, new[] { 1 }, null, ownerRv: 1);
        Assert.Equal(1, l.OwnerRv);
        Assert.True(l.CanAbort(GasOwner.Program));
        Assert.True(l.CanAbort(GasOwner.Program, 1));
        Assert.False(l.CanAbort(GasOwner.Program, 2));   // RV2 程序的中止不能停 RV1 的租约
        Assert.False(l.CanAbort(GasOwner.Leak, 1));
        Assert.True(l.TryEnd(GasOwner.Program));
        Assert.Null(l.OwnerRv);
        l.Begin(GasOwner.Leak, "泄漏测试", new[] { 2 }, new[] { 2 }, null);
        Assert.Null(l.OwnerRv);
    }

    [Fact]
    public void DifferentRv_IsNewLease_NotRenewal()
    {
        var l = new GasLease();
        l.Begin(GasOwner.Program, "RV1 程序 · 升压", new[] { 1 }, new[] { 1 }, null, ownerRv: 1);
        l.Invalidate(1);
        Assert.True(l.IsInvalidated(1));
        // 同占用者续租（同通道）：保留失效标记
        l.Begin(GasOwner.Program, "RV1 程序 · 稳压", new[] { 1 }, new[] { 1 }, null, ownerRv: 1);
        Assert.True(l.IsInvalidated(1));
        // 另一程序的租约：新占用者，失效标记清除
        l.Begin(GasOwner.Program, "RV2 程序 · 升压", new[] { 1, 2 }, new[] { 2 }, null, ownerRv: 2);
        Assert.False(l.IsInvalidated(1));
        Assert.Equal(2, l.OwnerRv);
    }

    private static GasChannel[] Idle8() => Enumerable.Range(1, 8)
        .Select(i => new GasChannel(i, ReactorState.Idle, false, false)).ToArray();

    private static readonly GasGuardInput Ok = new()
    {
        IoConfigured = true, NeedPressure = true, AnalogConfigured = true, PressOnline = true, GasTask = "", Channels = Idle8(),
    };

    [Fact]
    public void GasGuard_ProgramWait_SkipsBusyOnlyForProgramHolder()
    {
        var held = Ok with { GasTask = "RV1 程序 · 升压", GasHeldByProgram = true };
        Assert.Equal(OpCodes.GasBusy, GasGuard.Check(held)!.Value.Code);                       // 泄漏测试 / 手动路径：照旧拒绝
        Assert.Null(GasGuard.Check(held with { WaitInsteadOfBusy = true }));                    // 程序气路步骤：排队，不拒绝
        var leak = Ok with { GasTask = "泄漏测试", GasHeldByProgram = false, WaitInsteadOfBusy = true };
        Assert.Equal((OpCodes.GasBusy, "泄漏测试进行中，请等待完成或先停止"), GasGuard.Check(leak));   // 泄漏测试优先级不变
    }

    [Fact]
    public void GasGuard_OtherProgramChannels_PressingOrReact_NotBlocking_AlarmStillBlocks()
    {
        var ch = Idle8();
        ch[1] = new GasChannel(2, ReactorState.React, false, false);
        ch[2] = new GasChannel(3, ReactorState.Pressing, false, false);
        var i = Ok with { Channels = ch, SelfId = 1 };
        Assert.Equal(OpCodes.ChannelPressurized, GasGuard.Check(i)!.Value.Code);
        Assert.Null(GasGuard.Check(i with { ProgramOwnedIds = new[] { 2, 3 } }));
        Assert.Equal(OpCodes.ChannelPressurized, GasGuard.Check(i with { ProgramOwnedIds = new[] { 2 } })!.Value.Code);   // RV3 不是程序通道
        ch[1] = new GasChannel(2, ReactorState.Alarm, false, false);
        var r = GasGuard.Check(i with { Channels = ch, ProgramOwnedIds = new[] { 2, 3 } });
        Assert.Equal(OpCodes.ChannelPressurized, r!.Value.Code);
        Assert.Contains("RV2", r.Value.Message);   // 泄漏测试 / 手动路径（非程序排队模式）：任一报警仍阻塞，逐字同原来
    }

    /// <summary>联锁隔离（§7.12 第 7 条）：程序气路步骤不被其他通道的超温报警阻塞（只中止该通道自己的程序）；超压未解除仍一律阻塞；本通道报警仍阻塞。</summary>
    [Fact]
    public void GasGuard_ProgramWait_OtherChannelAlarm_DoesNotBlock_OverPressureStillBlocks()
    {
        var ch = Idle8();
        ch[2] = new GasChannel(3, ReactorState.Alarm, false, false);   // RV3 超温报警（运行中通道，不是空闲超温例外）
        var prog = Ok with { Channels = ch, SelfId = 1, WaitInsteadOfBusy = true, ProgramOwnedIds = Array.Empty<int>() };
        Assert.Null(GasGuard.Check(prog));                                        // RV1 程序租约不受 RV3 报警影响
        Assert.Equal(OpCodes.ChannelPressurized, GasGuard.Check(prog with { WaitInsteadOfBusy = false })!.Value.Code);   // 泄漏测试照旧
        ch[2] = new GasChannel(3, ReactorState.Alarm, true, false);              // RV3 超压未解除：共用总管，一律阻塞
        Assert.Equal(OpCodes.ChannelAlarm, GasGuard.Check(prog with { Channels = ch })!.Value.Code);
        ch[2] = new GasChannel(3, ReactorState.Idle, false, false);
        ch[0] = new GasChannel(1, ReactorState.Alarm, false, false);             // 本通道自己报警仍阻塞
        Assert.Equal(OpCodes.ChannelPressurized, GasGuard.Check(prog with { Channels = ch })!.Value.Code);
    }

    /// <summary>审查项：稳压脉冲间隙 / 程序排队期间，气路对泄漏测试与手动阀控仍视为占用（GAS_BUSY）；程序之间不受影响。</summary>
    [Fact]
    public void GasGuard_ProgramGasPending_BlocksNonProgramRequesters_Only()
    {
        var pending = Ok with { ProgramGasPending = "RV1 程序 · 稳压" };
        var r = GasGuard.Check(pending);
        Assert.Equal(OpCodes.GasBusy, r!.Value.Code);
        Assert.Equal("RV1 程序 · 稳压进行中，请等待完成或先停止", r.Value.Message);
        Assert.Null(GasGuard.Check(pending with { WaitInsteadOfBusy = true, SelfId = 2 }));   // 另一程序的气路步骤：排队，不拒绝
        Assert.Null(GasGuard.Check(Ok with { ProgramGasPending = "" }));
        // 真正的占用者文字优先于隐性占用
        var busy = pending with { GasTask = "泄漏测试" };
        Assert.Equal("泄漏测试进行中，请等待完成或先停止", GasGuard.Check(busy)!.Value.Message);
    }
}

/// <summary>运行信息：监视阶段文字同步到 Info（API 旧字段 program.phase 过渡期沿用）。</summary>
public class ProgramRunInfoPhaseTextTests
{
    [Fact]
    public void Default_IsPreparing()
    {
        var ri = new ProgramRunInfo(1, 3, "local", DateTimeOffset.UnixEpoch);
        Assert.Equal("准备执行", ri.PhaseText);
        Assert.Equal(-1, ri.Index);
    }
}
