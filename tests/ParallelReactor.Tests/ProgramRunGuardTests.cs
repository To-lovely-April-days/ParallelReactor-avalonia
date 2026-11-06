using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C3 / C6：程序下发守卫（下发时与确认时同一判定）。</summary>
public class ProgramRunGuardTests
{
    private static readonly ProgramRunInput Ok = new()
    {
        HasChannel = true, StepCount = 3, State = ReactorState.Ready, GasTask = "",
    };

    [Fact]
    public void AllClear_ReadyAndDoneAccepted()
    {
        Assert.Null(ProgramRunGuard.Reason(Ok));
        Assert.Null(ProgramRunGuard.Reason(Ok with { State = ReactorState.Done }));
    }

    [Fact]
    public void EachReason()
    {
        Assert.Contains("未选中", ProgramRunGuard.Reason(Ok with { HasChannel = false }));
        Assert.Contains("已有程序", ProgramRunGuard.Reason(Ok with { ProgramRunning = true }));
        Assert.Contains("空程序", ProgramRunGuard.Reason(Ok with { StepCount = 0 }));
        Assert.Contains("急停", ProgramRunGuard.Reason(Ok with { EstopLocked = true }));
        Assert.Contains("报警", ProgramRunGuard.Reason(Ok with { State = ReactorState.Alarm }));
        Assert.Contains("超温", ProgramRunGuard.Reason(Ok with { OverTempTripped = true }));
        Assert.Contains("超压", ProgramRunGuard.Reason(Ok with { OverPressureTripped = true }));
        Assert.Contains("停用", ProgramRunGuard.Reason(Ok with { State = ReactorState.Idle }));
        Assert.Contains("正在运行", ProgramRunGuard.Reason(Ok with { State = ReactorState.Heating }));
        Assert.Contains("正在运行", ProgramRunGuard.Reason(Ok with { State = ReactorState.Pressing }));
    }

    [Fact]
    public void GasBusy_BlocksEvenWithoutGasSteps()
    {
        var r = ProgramRunGuard.Reason(Ok with { GasBusy = true, GasTask = "泄漏测试", NeedsGas = false });
        Assert.Equal("泄漏测试进行中，请等待完成或先停止", r);
    }

    [Fact]
    public void GasBlock_OnlyWhenGasNeeded()
    {
        Assert.Null(ProgramRunGuard.Reason(Ok with { GasBlock = "阀门未配置", NeedsGas = false }));
        Assert.Equal("阀门未配置", ProgramRunGuard.Reason(Ok with { GasBlock = "阀门未配置", NeedsGas = true }));
    }

    [Fact]
    public void LimitErrors_ReportFirst()
    {
        Assert.Equal("第 01 步 升温：x", ProgramRunGuard.Reason(Ok with { LimitErrors = new[] { "第 01 步 升温：x" } }));
        var r = ProgramRunGuard.Reason(Ok with { LimitErrors = new[] { "A", "B", "C" } });
        Assert.StartsWith("A", r);
        Assert.Contains("另有 2 项", r);
        Assert.Null(ProgramRunGuard.Reason(Ok with { LimitErrors = new string[0] }));
    }

    [Fact]
    public void Precedence()
    {
        var all = Ok with
        {
            ProgramRunning = true, StepCount = 0, EstopLocked = true, State = ReactorState.Alarm,
            GasBusy = true, GasTask = "泄漏测试", NeedsGas = true, GasBlock = "g", LimitErrors = new[] { "L" },
        };
        Assert.Contains("已有程序", ProgramRunGuard.Reason(all));
        Assert.Contains("空程序", ProgramRunGuard.Reason(all with { ProgramRunning = false }));
        Assert.Contains("急停", ProgramRunGuard.Reason(all with { ProgramRunning = false, StepCount = 2 }));
        Assert.Contains("报警", ProgramRunGuard.Reason(all with { ProgramRunning = false, StepCount = 2, EstopLocked = false }));
        var s = all with { ProgramRunning = false, StepCount = 2, EstopLocked = false, State = ReactorState.Ready };
        Assert.Contains("泄漏测试", ProgramRunGuard.Reason(s));
        Assert.Equal("g", ProgramRunGuard.Reason(s with { GasBusy = false }));
        Assert.Equal("L", ProgramRunGuard.Reason(s with { GasBusy = false, GasBlock = null }));
        // 状态（运行中）优先于气路
        Assert.Contains("正在运行", ProgramRunGuard.Reason(s with { State = ReactorState.Heating }));
    }
}
