using System;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Heat;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>曲线升温（设计稿 curve-design 方向 B，2026-11-21）：分段计算与文字、检查、四个操作的守卫、演示数据的执行与推进。</summary>
public class HeatCurveTests
{
    private static readonly CurveSeg[] Four = { new(80, 20), new(80, 30), new(150, 35), new(150, 60) };

    [Fact]
    public void Rows_Rates_Kinds_Totals()
    {
        var rows = HeatCurve.Rows(Four, 25.6);
        Assert.Equal(new[] { CurveKind.Up, CurveKind.Hold, CurveKind.Up, CurveKind.Hold }, rows.Select(r => r.Kind));
        Assert.Equal(new[] { 0.0, 20, 50, 85 }, rows.Select(r => r.AtMin));
        Assert.Equal(new[] { 25.6, 80, 80, 150 }, rows.Select(r => r.FromC));
        Assert.Equal(new[] { "升 2.7 °C/min", "保温", "升 2.0 °C/min", "保温" }, rows.Select(HeatCurve.RateText));
        Assert.Equal(new[] { "升到 80 °C", "保温 80 °C", "升到 150 °C", "保温 150 °C" }, rows.Select(HeatCurve.StepText));
        Assert.Equal(145, HeatCurve.Total(Four));
        Assert.Equal(150, HeatCurve.Max(Four));
        Assert.Equal("共 4 段 · 2 h 25 min", HeatCurve.Head(Four));
        Assert.Equal(("50 min", "1 h 00 min", "2 h 05 min"), (HeatCurve.Dur(50), HeatCurve.Dur(60), HeatCurve.Dur(125)));
    }

    [Fact]
    public void At_FindsSegment_Fraction_Setpoint_AndStaysAtEnd()
    {
        var rows = HeatCurve.Rows(Four, 25.6);
        var a = HeatCurve.At(rows, 73)!;                      // 第 3 段：50 … 85 min，走了 23 / 35
        Assert.Equal(3, a.Row.I);
        Assert.Equal(23.0 / 35, a.F, 6);
        Assert.Equal(12, a.LeftMin, 6);
        Assert.Equal(126.0, a.SpC, 6);
        Assert.Equal((1, 0.0), (HeatCurve.At(rows, 0)!.Row.I, HeatCurve.At(rows, 0)!.F));
        Assert.Equal(2, HeatCurve.At(rows, 20)!.Row.I);        // 段的边界算下一段
        var end = HeatCurve.At(rows, 999)!;
        Assert.Equal((4, 1.0, 0.0, 150.0), (end.Row.I, end.F, end.LeftMin, end.SpC));
        Assert.Null(HeatCurve.At(Array.Empty<CurveRow>(), 1));
    }

    [Fact]
    public void InferStart_OnlyInFirstSegment()
    {
        // 第 1 段走了一半、给定 52.8：起点 = (52.8 − 80·0.5) / 0.5 = 25.6
        Assert.Equal(25.6, HeatCurve.InferStart(Four, 10, 52.8)!.Value, 6);
        Assert.Null(HeatCurve.InferStart(Four, 30, 80));       // 已经过了第 1 段
        Assert.Null(HeatCurve.InferStart(Four, 5, null));
        Assert.Null(HeatCurve.InferStart(Array.Empty<CurveSeg>(), 5, 50));
    }

    [Fact]
    public void Errors_SameRangesAsHmi()
    {
        Assert.Equal("", HeatCurve.Error(Four, 200));
        Assert.Equal("曲线至少要有 1 段", HeatCurve.Error(Array.Empty<CurveSeg>(), 200));
        Assert.Equal("曲线至少要有 1 段", HeatCurve.Error(null, 200));
        Assert.Equal("曲线最多 16 段", HeatCurve.Error(Enumerable.Repeat(new CurveSeg(50, 10), 17).ToArray(), 200));
        Assert.Equal("", HeatCurve.Error(Enumerable.Repeat(new CurveSeg(50, 10), 16).ToArray(), 200));
        Assert.Equal("第 2 段：目标温度须在 0 – 200 ℃", HeatCurve.Error(new[] { new CurveSeg(80, 20), new CurveSeg(201, 10) }, 200));
        Assert.Equal("第 1 段：用时须在 0.1 – 6000 min", HeatCurve.Error(new[] { new CurveSeg(80, 0) }, 200));
        Assert.Equal("第 1 段：用时须在 0.1 – 6000 min", HeatCurve.Error(new[] { new CurveSeg(80, 6001) }, 200));
        Assert.Equal("第 1 段：请输入用时", HeatCurve.Error(new[] { new CurveSeg(80, double.NaN) }, 200));
        Assert.Equal("第 1 段：请输入目标温度", HeatCurve.Error(new[] { new CurveSeg(double.NaN, 5) }, 200));
        Assert.Equal("第 1 段：目标温度须低于超温报警值 180 ℃", HeatCurve.Error(new[] { new CurveSeg(180, 5) }, 200, 180));
    }

    [Fact]
    public void Warn_FastRise_AndCoolingSegment()
    {
        var rows = HeatCurve.Rows(new[] { new CurveSeg(80, 20), new CurveSeg(150, 10), new CurveSeg(60, 30) }, 25);
        Assert.Equal(new[] { "", "升得太快，实际可能跟不上", "只能自然冷却，可能降不到" }, rows.Select(HeatCurve.Warn));
        Assert.Equal("降 3.0 °C/min", HeatCurve.RateText(rows[2]));
        Assert.Equal("降到 60 °C", HeatCurve.StepText(rows[2]));
    }

    // ———— 守卫 ————

    private static GuardFacts F(params ChannelFacts[] chans) => new()
    {
        Alias = "1号机", Lease = LeaseStatus.Remote, Model = ModelProfiles.Embedded.Get("PR8")!, Channels = chans,
    };

    [Fact]
    public void Guard_Table_Kinds_Permissions()
    {
        Assert.Equal((GuardKind.Control, true, "升温方式"), (DeviceActions.Spec(DeviceAction.HeatMode).Kind, DeviceActions.Spec(DeviceAction.HeatMode).PerChannel, DeviceActions.Label(DeviceAction.HeatMode)));
        Assert.Equal(GuardKind.Control, DeviceActions.Spec(DeviceAction.CurveSet).Kind);
        Assert.Equal(GuardKind.Control, DeviceActions.Spec(DeviceAction.CurveStart).Kind);
        Assert.Equal(GuardKind.Safe, DeviceActions.Spec(DeviceAction.CurveStop).Kind);     // 停止类：不要控制权
        Assert.Equal(TokenPermission.Stop, TokenRole.PermissionOf(DeviceAction.CurveStop));
        Assert.Equal(TokenPermission.Control, TokenRole.PermissionOf(DeviceAction.CurveStart));
        Assert.True(DeviceActions.TryParse("curveStart", out var a) && a == DeviceAction.CurveStart);
    }

    [Fact]
    public void Guard_Reasons()
    {
        var f = F(
            new ChannelFacts(1, true, StateKey.React, Heating: true, Program: ProgramStatus.Running),
            new ChannelFacts(2, true, StateKey.Ready),
            new ChannelFacts(3, true, StateKey.Idle),
            new ChannelFacts(4, true, StateKey.Heat, Heating: true, CurveMode: true, CurveRunning: true),
            new ChannelFacts(5, true, StateKey.Heat, Heating: true),
            new ChannelFacts(6, true, StateKey.Alarm),
            new ChannelFacts(7, false));
        ActionPayload Mode(int n, string m) => new(N: n, Id: m);
        ActionPayload Set(int n) => new(N: n, Segs: Four);

        Assert.Equal("", ActionGuard.Why(f, DeviceAction.HeatMode, Mode(2, "curve")));
        Assert.Equal("没有指定升温方式", ActionGuard.Why(f, DeviceAction.HeatMode, ActionPayload.Ch(2)));
        Assert.Equal("RV1 程序运行中", ActionGuard.Why(f, DeviceAction.HeatMode, Mode(1, "curve")));
        Assert.Equal("RV4 曲线运行中 · 先停止曲线", ActionGuard.Why(f, DeviceAction.HeatMode, Mode(4, "fixed")));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.HeatMode, Mode(4, "curve")));                 // 已经是曲线：不算切换
        Assert.Equal("RV5 加热中 · 先停止加热", ActionGuard.Why(f, DeviceAction.HeatMode, Mode(5, "curve")));
        Assert.Equal("RV7 未安装", ActionGuard.Why(f, DeviceAction.HeatMode, Mode(7, "curve")));

        Assert.Equal("", ActionGuard.Why(f, DeviceAction.CurveSet, Set(2)));
        Assert.Equal("RV1 程序运行中", ActionGuard.Why(f, DeviceAction.CurveSet, Set(1)));
        Assert.Equal("RV4 曲线运行中 · 先停止曲线", ActionGuard.Why(f, DeviceAction.CurveSet, Set(4)));
        Assert.Equal("曲线至少要有 1 段", ActionGuard.Why(f, DeviceAction.CurveSet, ActionPayload.Ch(2)));
        Assert.Equal("第 1 段：目标温度须在 0 – 200 ℃", ActionGuard.Why(f, DeviceAction.CurveSet, new ActionPayload(N: 2, Segs: new[] { new CurveSeg(300, 5) })));

        Assert.Equal("", ActionGuard.Why(f, DeviceAction.CurveStart, ActionPayload.Ch(2)));
        Assert.Equal("RV3 已停用 · 先启用", ActionGuard.Why(f, DeviceAction.CurveStart, ActionPayload.Ch(3)));
        Assert.Equal("RV6 报警中 · 先处理", ActionGuard.Why(f, DeviceAction.CurveStart, ActionPayload.Ch(6)));
        Assert.Equal("RV1 程序运行中", ActionGuard.Why(f, DeviceAction.CurveStart, ActionPayload.Ch(1)));
        Assert.Equal("RV4 曲线已在运行", ActionGuard.Why(f, DeviceAction.CurveStart, ActionPayload.Ch(4)));
        Assert.Equal("RV5 定值加热中 · 先停止加热", ActionGuard.Why(f, DeviceAction.CurveStart, ActionPayload.Ch(5)));

        // 停止曲线：没在走也能发；不要控制权
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.CurveStop, ActionPayload.Ch(2)));
        var local = f with { Lease = LeaseStatus.Local };
        Assert.Equal("", ActionGuard.Why(local, DeviceAction.CurveStop, ActionPayload.Ch(4)));
        Assert.Equal("1号机为本地只读 · 先申请控制权", ActionGuard.Why(local, DeviceAction.CurveStart, ActionPayload.Ch(2)));
    }

    // ———— 演示数据 ————

    [Fact]
    public void Demo_Set_Start_Tick_Stop()
    {
        var f = new DemoFleet(DemoParams.Parse("fusion=1"));
        var d = f.Device(1)!;
        f.SeedCurve(d, 5, run: false, free: new[] { 6 });
        var ch = d.Channel(5)!;
        Assert.True(ch.CurveMode);
        Assert.False(ch.CurveRunning);
        Assert.Equal(4, ch.CurveSegs.Count);
        Assert.Equal(StateKey.Ready, d.Channel(6)!.State);

        var segs = new[] { new CurveSeg(60, 1), new CurveSeg(60, 2) };
        var set = f.Act(d, DeviceAction.CurveSet, new ActionPayload(N: 5, Segs: segs), quiet: true);
        Assert.Equal((true, "RV5 曲线已更新 · 共 2 段"), (set.Ok, set.Message));
        double t0 = ch.TempC;
        var start = f.Act(d, DeviceAction.CurveStart, ActionPayload.Ch(5), quiet: true);
        Assert.Equal((true, "RV5 曲线升温已启动 · 共 2 段"), (start.Ok, start.Message));
        Assert.True(ch.CurveRunning && ch.Heating);
        Assert.Equal(StateKey.Heat, ch.State);
        var r0 = ch.Curve(f.Now);
        Assert.Equal((true, true, 3.0, false, t0), (r0.CurveMode, r0.Running, r0.TotalMin, r0.Complete, r0.StartC!.Value));
        // 运行中不能改、不能切回定值
        Assert.Equal("RV5 曲线运行中 · 先停止曲线", f.Act(d, DeviceAction.CurveSet, new ActionPayload(N: 5, Segs: segs), quiet: true).Message);
        Assert.Equal("RV5 曲线运行中 · 先停止曲线", f.Act(d, DeviceAction.HeatMode, new ActionPayload(N: 5, Id: "fixed"), quiet: true).Message);

        // 走半分钟：给定值在起点和 60 ℃ 之间，实测跟着往上
        for (int i = 0; i < 30; i++) f.Tick();
        var r1 = ch.Curve(f.Now);
        Assert.InRange(r1.ElapsedMin, 0.4, 0.6);
        Assert.InRange(ch.SpC!.Value, t0 + 5, 59);
        Assert.True(ch.TempC > t0);
        // 走完：保持最后一段的温度，仍算在运行
        for (int i = 0; i < 200; i++) f.Tick();
        var r2 = ch.Curve(f.Now);
        Assert.True(r2.Running && r2.Complete);
        Assert.Equal(60, ch.SpC);

        var stop = f.Act(d, DeviceAction.CurveStop, ActionPayload.Ch(5), quiet: true);
        Assert.Equal((true, "RV5 曲线升温已停止 · 保持当前给定值", false), (stop.Ok, stop.Message, stop.Info));
        Assert.False(ch.CurveRunning);
        Assert.True(ch.Heating);                                   // 停曲线不停加热
        Assert.True(f.Act(d, DeviceAction.CurveStop, ActionPayload.Ch(5), quiet: true).Info);   // 没在走：照发，info
        // 停止加热 / 停止通道把曲线也停掉
        f.Act(d, DeviceAction.CurveStart, ActionPayload.Ch(5), quiet: true);
        f.Act(d, DeviceAction.HeatStop, ActionPayload.Ch(5), quiet: true);
        Assert.False(ch.CurveRunning);
        Assert.True(f.Act(d, DeviceAction.HeatMode, new ActionPayload(N: 5, Id: "fixed"), quiet: true).Ok);
        Assert.False(ch.CurveMode);
    }
}
