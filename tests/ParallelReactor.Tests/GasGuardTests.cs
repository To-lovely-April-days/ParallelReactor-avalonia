using System.Linq;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>开始气路序列公共检查：错误码与今天逐字相同的文字、selfId 豁免、停用通道超温报警不阻塞。</summary>
public class GasGuardTests
{
    private static GasChannel[] Idle8() => Enumerable.Range(1, 8)
        .Select(i => new GasChannel(i, ReactorState.Idle, false, false)).ToArray();

    private static readonly GasGuardInput Ok = new()
    {
        IoConfigured = true, NeedPressure = true, AnalogConfigured = true, PressOnline = true,
        GasTask = "", Channels = Idle8(),
    };

    private static GasGuardInput With(params GasChannel[] changed)
    {
        var ch = Idle8();
        foreach (var c in changed) ch[c.Id - 1] = c;
        return Ok with { Channels = ch };
    }

    [Fact]
    public void AllClear() => Assert.Null(GasGuard.Check(Ok));

    [Fact]
    public void EachBranch_CodeAndText()
    {
        Assert.Equal((OpCodes.EstopActive, "急停状态下禁止气路操作"), GasGuard.Check(Ok with { EstopLocked = true }));
        Assert.Equal((OpCodes.HwNotConfigured, "阀门模块未配置串口（PR_IO_PORT），无法执行气路操作"), GasGuard.Check(Ok with { IoConfigured = false }));
        Assert.Equal((OpCodes.HwNotConfigured, "压力采集未配置串口（PR_PRESS_PORT），无法闭环充压与超压监视"), GasGuard.Check(Ok with { AnalogConfigured = false }));
        Assert.Equal((OpCodes.HwOffline, "压力采集离线（8AI 通讯失败），请检查接线与串口后再试"), GasGuard.Check(Ok with { PressOnline = false }));
        Assert.Equal((OpCodes.GasBusy, "泄漏测试进行中，请等待完成或先停止"), GasGuard.Check(Ok with { GasTask = "泄漏测试" }));
        Assert.Equal((OpCodes.ManualValveActive, "请先关闭「手动阀控」：自动序列与手动阀操作不能同时进行"), GasGuard.Check(Ok with { ManualValve = true }));
        Assert.Equal((OpCodes.ChannelAlarm, "RV2、RV5 超压未解除（需低于超压报警值的 98%），禁止气路操作"),
            GasGuard.Check(With(new GasChannel(2, ReactorState.Alarm, true, false), new GasChannel(5, ReactorState.Done, true, false))));
        Assert.Equal((OpCodes.ChannelPressurized, "RV1、RV4 正在加压 / 反应 / 报警，需先停止"),
            GasGuard.Check(With(new GasChannel(1, ReactorState.Pressing, false, false), new GasChannel(4, ReactorState.Alarm, false, false))));
    }

    [Fact]
    public void PressureChecks_SkippedWhenNotNeeded()
        => Assert.Null(GasGuard.Check(Ok with { NeedPressure = false, AnalogConfigured = false, PressOnline = false }));

    [Fact]
    public void SelfId_ExemptsPressingAndReact_NotAlarm()
    {
        var pressing = With(new GasChannel(3, ReactorState.React, false, false));
        Assert.NotNull(GasGuard.Check(pressing));
        Assert.Null(GasGuard.Check(pressing with { SelfId = 3 }));
        var alarm = With(new GasChannel(3, ReactorState.Alarm, false, false)) with { SelfId = 3 };
        Assert.Equal(OpCodes.ChannelPressurized, GasGuard.Check(alarm)!.Value.Code);
    }

    [Fact]
    public void IdleOverTempAlarm_DoesNotBlock()
        => Assert.Null(GasGuard.Check(With(new GasChannel(6, ReactorState.Alarm, false, true))));

    [Fact]
    public void Order_EstopBeforeConfig()
        => Assert.Equal(OpCodes.EstopActive, GasGuard.Check(Ok with { EstopLocked = true, IoConfigured = false, GasTask = "x" })!.Value.Code);
}
