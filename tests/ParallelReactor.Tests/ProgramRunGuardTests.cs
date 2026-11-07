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
    public void ProgramRunning_IsPerChannel_AndConcurrencyCap()
    {
        Assert.Equal("该通道已有程序在执行 · 请等待完成或先中止", ProgramRunGuard.Reason(Ok with { ProgramRunning = true }));
        Assert.Null(ProgramRunGuard.Reason(Ok with { RunningCount = 7 }));   // 其他通道在跑不阻塞本通道
        var full = ProgramRunGuard.Check(Ok with { RunningCount = ProgramRunSet.MaxConcurrent });
        Assert.Equal(ParallelReactor.Commands.OpCodes.ProgramRunning, full!.Value.Code);
        Assert.Contains("并行程序已达 8 个", full.Value.Message);
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

/// <summary>批 C：ProgramRunGuard.Check 的 §11 错误码（Reason 的文字不变）。</summary>
public class ProgramRunGuardCodeTests
{
    private static readonly ProgramRunInput Ok = new()
    {
        HasChannel = true, StepCount = 3, State = ReactorState.Ready, GasTask = "",
    };

    [Fact]
    public void AllClear_IsNull() => Assert.Null(ProgramRunGuard.Check(Ok));

    [Theory]
    [InlineData("nochannel", ParallelReactor.Commands.OpCodes.ChannelNotFound)]
    [InlineData("running", ParallelReactor.Commands.OpCodes.ProgramRunning)]
    [InlineData("empty", ParallelReactor.Commands.OpCodes.ProgramInvalid)]
    [InlineData("estop", ParallelReactor.Commands.OpCodes.EstopActive)]
    [InlineData("alarm", ParallelReactor.Commands.OpCodes.ChannelAlarm)]
    [InlineData("ot", ParallelReactor.Commands.OpCodes.ChannelAlarm)]
    [InlineData("idle", ParallelReactor.Commands.OpCodes.ChannelStateInvalid)]
    [InlineData("heating", ParallelReactor.Commands.OpCodes.ChannelBusy)]
    [InlineData("gasbusy", ParallelReactor.Commands.OpCodes.GasBusy)]
    [InlineData("limits", ParallelReactor.Commands.OpCodes.ParamOutOfRange)]
    public void EachBranch_HasCode(string branch, string code)
    {
        var i = branch switch
        {
            "nochannel" => Ok with { HasChannel = false },
            "running" => Ok with { ProgramRunning = true },
            "empty" => Ok with { StepCount = 0 },
            "estop" => Ok with { EstopLocked = true },
            "alarm" => Ok with { State = ReactorState.Alarm },
            "ot" => Ok with { OverTempTripped = true },
            "idle" => Ok with { State = ReactorState.Idle },
            "heating" => Ok with { State = ReactorState.Heating },
            "gasbusy" => Ok with { GasBusy = true, GasTask = "泄漏测试" },
            _ => Ok with { LimitErrors = new[] { "升温 超限" } },
        };
        var r = ProgramRunGuard.Check(i);
        Assert.Equal(code, r!.Value.Code);
        Assert.Equal(ProgramRunGuard.Reason(i), r.Value.Message);   // 文字与 Reason 同源
    }

    [Fact]
    public void GasBlock_CarriesGuardCode_DefaultsToGasBusy()
    {
        var i = Ok with { NeedsGas = true, GasBlock = "阀门离线", GasBlockCode = ParallelReactor.Commands.OpCodes.HwOffline };
        Assert.Equal(ParallelReactor.Commands.OpCodes.HwOffline, ProgramRunGuard.Check(i)!.Value.Code);
        Assert.Equal(ParallelReactor.Commands.OpCodes.GasBusy, ProgramRunGuard.Check(i with { GasBlockCode = null })!.Value.Code);
    }
}
