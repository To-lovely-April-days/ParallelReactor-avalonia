using ParallelReactor.Commands;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>启动加热公共检查：错误码与今天逐字相同的文字、判断顺序。</summary>
public class HeatGuardTests
{
    private static readonly HeatGuardInput Ok = new()
    {
        Id = 3, TempConfigured = true, TempOnline = true, StopHeatOnOverPressure = true,
        OverTemp = 250, OverPressure = 1000,
    };

    [Fact]
    public void AllClear() => Assert.Null(HeatGuard.Check(Ok));

    [Fact]
    public void EachBranch_CodeAndText()
    {
        Assert.Equal((OpCodes.EstopActive, "急停状态下不可启动加热 · 请先复位"), HeatGuard.Check(Ok with { EstopLocked = true }));
        Assert.Equal((OpCodes.HwNotConfigured, "温控未配置串口（PR_TEMP_PORT），无法加热"), HeatGuard.Check(Ok with { TempConfigured = false }));
        Assert.Equal((OpCodes.HwOffline, "温控离线（AI-8 通讯失败），无法启动加热"), HeatGuard.Check(Ok with { TempOnline = false }));
        Assert.Equal((OpCodes.ChannelAlarm, "RV3 超温切断未解除（需低于 248℃），不可启动加热"),
            HeatGuard.Check(Ok with { OverTempTripped = true }));
        Assert.Equal((OpCodes.ChannelAlarm, "RV3 超温切断未解除（需低于 247.5℃），不可启动加热"),
            HeatGuard.Check(Ok with { OverTempTripped = true, OverTemp = 249.5 }));
        Assert.Equal((OpCodes.ChannelAlarm, $"RV3 超压联锁未解除（需低于 {Units.FormatP(980)}），不可启动加热"),
            HeatGuard.Check(Ok with { OverPressureTripped = true }));
    }

    [Fact]
    public void OverPressure_OnlyWhenStopHeatEnabled()
        => Assert.Null(HeatGuard.Check(Ok with { OverPressureTripped = true, StopHeatOnOverPressure = false }));

    [Fact]
    public void ChannelChecks_SkippedWithoutId()
        => Assert.Null(HeatGuard.Check(Ok with { Id = null, OverTempTripped = true, OverPressureTripped = true }));

    [Fact]
    public void Order_EstopFirst_ThenConfig_ThenOnline_ThenOverTemp_ThenOverPressure()
    {
        var all = Ok with { EstopLocked = true, TempConfigured = false, TempOnline = false, OverTempTripped = true, OverPressureTripped = true };
        Assert.Equal(OpCodes.EstopActive, HeatGuard.Check(all)!.Value.Code);
        Assert.Equal(OpCodes.HwNotConfigured, HeatGuard.Check(all with { EstopLocked = false })!.Value.Code);
        Assert.Equal(OpCodes.HwOffline, HeatGuard.Check(all with { EstopLocked = false, TempConfigured = true })!.Value.Code);
        Assert.Contains("超温", HeatGuard.Check(all with { EstopLocked = false, TempConfigured = true, TempOnline = true })!.Value.Message);
    }
}
