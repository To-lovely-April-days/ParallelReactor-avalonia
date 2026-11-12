using System;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Programs.Progress（样稿 PR.sim.progress / segments、ui.progText 长短两种写法、ui.stepBar、步骤卡标记）：剩余时间、等条件、等待进气、
/// 离线冻结、报警暂停、中止、已结束、已下发，以及真机的报警中止。演示数据里每个反应釜的同一组结果另由 DemoFleetTests 逐条对照样稿 JS。
/// </summary>
public class ProgressTests
{
    private static readonly ModelProfile Pr84 = ModelProfiles.Embedded.Get("PR8-4")!;
    private static readonly ModelProfile Pr4 = ModelProfiles.Embedded.Get("PR4")!;
    private static readonly DateTime Now = DemoFleet.T0;

    private static ProgramDef Lib(ModelProfile m, string id) => DemoFleet.ProgramLibrary(m).First(p => p.Id == id);

    /// <summary>加氢-标准（升温 20 / 充压 6 / 反应 72 / 结束 15 min），第 idx 步，当前步开始于 startedAgoMin 分钟前、预计 durMin 分钟。</summary>
    private static ProgramRecord Rec(int idx, double startedAgoMin, double durMin, ProgramStatus status = ProgramStatus.Running, ModelProfile? m = null, string id = "h2-std")
    {
        var def = Lib(m ?? Pr84, id);
        var stepStart = Now.AddMinutes(-startedAgoMin);
        var log = Enumerable.Range(0, idx).Select(i => new StepLogEntry(i, stepStart.AddMinutes(-(idx - i) * 10), stepStart.AddMinutes(-(idx - i - 1) * 10))).ToArray();
        return new ProgramRecord
        {
            Id = def.Id, Name = def.Name, Steps = def.Steps, StepIndex = idx, Status = status, Log = log,
            StartedAt = stepStart.AddMinutes(-idx * 10), StepStartedAt = stepStart, StepDurS = durMin * 60,
            EndedAt = status is ProgramStatus.Done or ProgramStatus.Aborted or ProgramStatus.Failed ? Now.AddMinutes(-1) : null,
        };
    }

    private static ProgressContext Ctx(StateKey st = StateKey.React, bool online = true, DateTime? dataAt = null, ModelProfile? m = null, Func<string, bool>? gas = null, string? word = null) =>
        new(Now, online, dataAt, st, m ?? Pr84, gas, word ?? Progress.WaitGasWordDemo);

    [Fact]
    public void Running_RemainingTimes()
    {
        var p = Rec(2, 30, 72);
        var pr = Progress.Of(p, Ctx())!;
        Assert.Equal(("加氢-标准", "运行中", 3, 4, "反应"), (pr.Name, pr.StatusText, pr.K, pr.N, pr.StepName));
        Assert.Equal(42 * 60, pr.StepRemainS);
        Assert.Equal(42, pr.StepRemainMin);
        Assert.Equal(42 + 15, pr.TotalRemainMin);           // 后面还有「结束」15 min
        Assert.Equal(30.0 / 72, pr.Within, 12);
        Assert.Equal((20 + 6 + 30) / (20.0 + 6 + 72 + 15), pr.Frac, 12);
        Assert.False(pr.Overdue || pr.Offline || pr.WaitGas);
        Assert.Equal("剩 42 min", pr.RemainText);
        Assert.Equal("第 3/4 步 · 剩 42 min", Progress.Text(pr));
        Assert.Equal("3/4 · 42 min", Progress.Text(pr, shortForm: true));
        Assert.Equal("当前 · 剩 42 min", Progress.StepMark(p, 2, pr));
        Assert.Equal("已完成", Progress.StepMark(p, 0, pr));
        Assert.Equal("", Progress.StepMark(p, 3, pr));
    }

    [Fact]
    public void RemainingRoundsUp()
    {
        var pr = Progress.Of(Rec(2, 30.5, 72), Ctx())!;      // 剩 41.5 min
        Assert.Equal(42, pr.StepRemainMin);
    }

    [Fact]
    public void Overdue_TemperatureCondition()
    {
        var p = Rec(3, 20, 15);                              // 结束步骤：温度 < 50 ℃，预计 15 min 已用完
        var pr = Progress.Of(p, Ctx(StateKey.Cool))!;
        Assert.True(pr.Overdue);
        Assert.Equal(0, pr.StepRemainMin);
        Assert.Equal("等温度 < 50 ℃", pr.RemainText);
        Assert.Equal("→ 50 ℃", pr.RemainShort);
        Assert.Equal("第 4/4 步 · 等温度 < 50 ℃", Progress.Text(pr));
        Assert.Equal("4/4 · → 50 ℃", Progress.Text(pr, true));
        Assert.Equal("当前 · 等条件", Progress.StepMark(p, 3, pr));
    }

    [Fact]
    public void Overdue_PressureCondition_UsesModelDecimals()
    {
        var p = Rec(1, 10, 6, m: Pr4, id: "h2-hp");          // 充压到 5.2 MPa，预计 6 min 已用完
        var pr = Progress.Of(p, Ctx(StateKey.Press, m: Pr4))!;
        Assert.Equal("等压力 ≥ 5.20 MPa", pr.RemainText);
        Assert.Equal("→ 5.20 MPa", pr.RemainShort);
        Assert.False(Progress.Of(Rec(2, 80, 72), Ctx())!.Overdue);   // 按时间结束的步骤不写「等条件」
    }

    [Fact]
    public void WaitGas()
    {
        var p = Rec(1, 2, 6);                                // 充压：气路关着 → 等待进气
        var pr = Progress.Of(p, Ctx(StateKey.Wait))!;
        Assert.True(pr.WaitGas);
        Assert.Equal("气体 A", pr.GasName);
        Assert.Equal("第 2/4 步 · 等待进气", Progress.Text(pr));
        Assert.Equal("2/4 · 等待进气", Progress.Text(pr, true));
        Assert.Equal("等待进气", Progress.StepMark(p, 1, pr));
        Assert.False(Progress.Bar(p, pr, online: true)[1].Pulse);   // 等进气时不呼吸
        var live = Progress.Of(p, Ctx(StateKey.Wait, word: StateMap.WaitGasWord))!;   // 真机：排队等共用气路
        Assert.Equal("第 2/4 步 · 等待气路", Progress.Text(live));
        Assert.False(Progress.Of(p, Ctx(StateKey.Press))!.WaitGas);
    }

    [Fact]
    public void Offline_FreezesAtDataAt()
    {
        var p = Rec(2, 30, 72);
        var pr = Progress.Of(p, Ctx(online: false, dataAt: Now.AddMinutes(-10)))!;   // 10 min 前断线
        Assert.True(pr.Offline);
        Assert.Equal(52, pr.StepRemainMin);                  // 停在断线那一刻，不再倒计时
        Assert.Equal("离线 · 停在第 3/4 步", Progress.Text(pr));
        Assert.Equal("3/4 · 离线", Progress.Text(pr, true));
        Assert.Equal("当前（离线）", Progress.StepMark(p, 2, pr));
        Assert.False(Progress.Bar(p, pr, online: false)[2].Pulse);
        var segs = Progress.Segments(p, Ctx(online: false, dataAt: Now.AddMinutes(-10)));
        Assert.Equal(Now.AddMinutes(42), segs.Single(s => s.State == SegmentState.Cur).T1);
    }

    [Fact]
    public void Held()
    {
        var p = Rec(1, 2, 6, ProgramStatus.Held);
        var pr = Progress.Of(p, Ctx(StateKey.Alarm))!;
        Assert.Equal("报警暂停", pr.StatusText);
        Assert.Equal("第 2/4 步 · 报警暂停", Progress.Text(pr));
        Assert.Equal("2/4 · 暂停", Progress.Text(pr, true));
        Assert.Equal("报警暂停", Progress.StepMark(p, 1, pr));
        Assert.False(pr.Overdue);                            // 只有运行中才算等条件
        var bar = Progress.Bar(p, pr, online: true);
        Assert.Equal((StepBarState.Held, false), (bar[1].State, bar[1].Pulse));
        Assert.Equal(SegmentState.Cur, Progress.StepState(p, 1));
    }

    [Fact]
    public void Aborted()
    {
        var p = Rec(2, 30, 72, ProgramStatus.Aborted);
        var pr = Progress.Of(p, Ctx(StateKey.Stopped))!;
        Assert.Equal(("已中止", 0.0, 0), (pr.StatusText, pr.StepRemainS, pr.TotalRemainMin));
        Assert.Equal("已中止 · 第 3/4 步", Progress.Text(pr));
        Assert.Equal("中止 · 3/4", Progress.Text(pr, true));
        Assert.Equal("已中止", Progress.StepMark(p, 2, pr));
        Assert.Equal(SegmentState.Abort, Progress.StepState(p, 2));
        var segs = Progress.Segments(p, Ctx());
        Assert.Equal(new[] { SegmentState.Done, SegmentState.Done, SegmentState.Abort }, segs.Select(s => s.State));
        Assert.Equal(p.EndedAt, segs[^1].T1);
        Assert.Equal((StepBarState.Abort, 100.0), (Progress.Bar(p, pr, true)[2].State, Progress.Bar(p, pr, true)[2].FillPct));
    }

    [Fact]
    public void Done()
    {
        var p = Rec(3, 5, 4, ProgramStatus.Done);
        var pr = Progress.Of(p, Ctx(StateKey.Done))!;
        Assert.Equal(("已结束", 1.0, 1.0), (pr.StatusText, pr.Within, pr.Frac));
        Assert.Equal("已结束 · 共 4 步", Progress.Text(pr));
        Assert.Equal("已结束 · 4 步", Progress.Text(pr, true));
        Assert.All(Progress.Bar(p, pr, true), b => Assert.Equal(StepBarState.Done, b.State));
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal("已完成", Progress.StepMark(p, i, pr)));
        Assert.All(Progress.Segments(p, Ctx()), s => Assert.Equal(SegmentState.Done, s.State));
    }

    [Fact]
    public void Loaded()
    {
        var def = Lib(Pr84, "h2-std");
        var p = new ProgramRecord { Id = def.Id, Name = def.Name, Steps = def.Steps, Status = ProgramStatus.Loaded };
        var pr = Progress.Of(p, Ctx(StateKey.Ready))!;
        Assert.Equal("已下发 · 待运行", pr.StatusText);
        Assert.Equal("已下发 · 待运行", Progress.Text(pr));
        Assert.Equal("待运行", Progress.Text(pr, true));
        Assert.Empty(Progress.Segments(p, Ctx()));           // 还没开始
        Assert.Equal((1, 0.0), (pr.K, pr.Frac));
    }

    [Fact]
    public void Failed_LiveAlarmAbort()
    {
        var p = Rec(1, 2, 6, ProgramStatus.Failed);
        var pr = Progress.Of(p, Ctx(StateKey.Alarm))!;
        Assert.Equal("报警中止", pr.StatusText);
        Assert.Equal("报警中止 · 第 2/4 步", Progress.Text(pr));
        Assert.Equal("中止 · 2/4", Progress.Text(pr, true));
        Assert.Equal(SegmentState.Abort, Progress.StepState(p, 1));
        Assert.Equal(StepBarState.Abort, Progress.Bar(p, pr, true)[1].State);   // 进度条灰色，不用红色
    }

    [Fact]
    public void NoProgram()
    {
        Assert.Null(Progress.Of(null, Ctx()));
        Assert.Equal("无程序", Progress.Text(null));
        Assert.Equal("无程序", Progress.Text(null, true));
        Assert.Empty(Progress.Segments(null, Ctx()));
        Assert.Empty(Progress.Bar(null, null, true));
    }

    [Fact]
    public void Segments_CurrentStepReachesPastNow_NextStepsPlanned()
    {
        var p = Rec(1, 10, 6);                               // 充压预计 6 min，已用 10 min
        var segs = Progress.Segments(p, Ctx(StateKey.Press, gas: k => k != "a"));
        var cur = segs.Single(s => s.State == SegmentState.Cur);
        Assert.Equal(Now.AddSeconds(30), cur.T1);            // 至少延到「现在 + 30 s」
        var next = segs.Where(s => s.State == SegmentState.Next).ToArray();
        Assert.Equal(new[] { "反应", "结束" }, next.Select(s => s.Name));
        Assert.Equal(cur.T1.AddMinutes(72), next[0].T1);     // 反应按 minutes
        Assert.Equal(next[0].T1.AddMinutes(15), next[1].T1);
        Assert.False(next[0].WaitGas);                       // 只有充压 / 吹扫要进气
        var before = Progress.Segments(Rec(0, 5, 20), Ctx(StateKey.Heat, gas: k => k != "a"));
        Assert.True(before.Single(s => s.Name == "充压").WaitGas);   // 下一步要进气、进气口关着
        Assert.False(Progress.Segments(Rec(0, 5, 20), Ctx(StateKey.Heat)).Single(s => s.Name == "充压").WaitGas);   // 不给气路状态 = 不提示
    }

    [Fact]
    public void StepPlanS_Rules()
    {
        Assert.Equal(60, Progress.StepPlanS(null));
        Assert.Equal(72 * 60, Progress.StepPlanS(new ProgramStep(StepKind.React) { Minutes = 72, EstMin = 5 }));
        Assert.Equal(5 * 60, Progress.StepPlanS(new ProgramStep(StepKind.Wait) { Minutes = 5 }));
        Assert.Equal(20 * 60, Progress.StepPlanS(new ProgramStep(StepKind.Heat) { EstMin = 20 }));
        Assert.Equal(60, Progress.StepPlanS(new ProgramStep(StepKind.Heat) { EstMin = 0.5 }));   // 至少 60 s
        Assert.Equal(60, Progress.StepPlanS(new ProgramStep(StepKind.End)));                     // 未知 = 1 min
    }

    [Fact]
    public void BarLabel() => Assert.Equal("加氢-标准 第 3/4 步", Progress.BarLabel(Rec(2, 30, 72)));
}
