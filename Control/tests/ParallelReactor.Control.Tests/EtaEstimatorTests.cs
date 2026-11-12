using System;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Programs.EtaEstimator：HMI 消息里的倒计时（定时步骤、升温的平衡段，PC 中途连上也准）、曲线升温按 profile、按条件结束的步骤按读数 ÷ 速率估
/// （实测速率优先）、估计用完时剩余为 0 → Progress 写「等条件」、计划时长（后面步骤与 ETA）、实测速率、断线时 Progress 停住。
/// 结果经 ProgramRecord + Progress（与演示数据同一套）检查页面上会看到的文字。
/// </summary>
public class EtaEstimatorTests
{
    private static readonly DateTime D0 = new(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc);
    private static readonly ModelProfile Pr8 = ModelProfiles.Embedded.Get("PR8")!;

    private static readonly ProgramStep Heat = new(StepKind.Heat) { Sp = 120, End = new StepEnd(EndKind.Time, 10), EstMin = 10 };
    private static readonly ProgramStep Press = new(StepKind.Press) { Sp = 120, Target = 0.35, Gas = "a", End = new StepEnd(EndKind.Press, 0.35, "≥") };
    private static readonly ProgramStep React = new(StepKind.React) { Sp = 120, Target = 0.35, Gas = "a", Minutes = 72, End = new StepEnd(EndKind.Time, 72), EstMin = 72 };
    private static readonly ProgramStep End = new(StepKind.End) { CoolTo = 50, Vent = true, End = new StepEnd(EndKind.Temp, 50, "<") };
    private static readonly ProgramStep[] Steps = { Heat, Press, React, End };

    private static ProgressInfo Prog(ProgramRecord r, DateTime nowUtc, bool online = true, DateTime? dataAtUtc = null, StateKey state = StateKey.React) =>
        Progress.Of(r, new ProgressContext(nowUtc.ToLocalTime(), online, dataAtUtc?.ToLocalTime(), state, Pr8))!;

    private static ProgramRecord Running(int idx) => new() { Name = "加氢测试", Steps = Steps, StepIndex = idx, Status = ProgramStatus.Running, StartedAt = D0.ToLocalTime(), Source = ProgramSource.Ledger };

    [Theory]
    [InlineData("03/04 反应 · 反应中 · 剩 12.5 分", 12.5)]
    [InlineData("01/04 升温 · 已到 120℃ · 平衡剩 3 分", 3.0)]
    [InlineData("04/05 搅拌 · 1000 rpm · 剩 0 分", 0.0)]
    [InlineData("02/05 等待 · 固定时长 · 剩 29.9 分", 29.9)]
    [InlineData("01/04 升温 · 升温 80.5℃ → 120℃", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RemainFromMessage(string? message, double? minutes)
    {
        Assert.Equal(minutes, EtaEstimator.RemainFromMessage(message));
    }

    [Fact]
    public void Balancing_AndPurgeCycle()
    {
        Assert.True(EtaEstimator.Balancing("01/04 升温 · 已到 120℃ · 平衡剩 3 分"));
        Assert.False(EtaEstimator.Balancing("03/04 反应 · 反应中 · 剩 12.5 分"));
        Assert.Equal((2, 3), EtaEstimator.PurgeCycle("01/04 吹扫 · 惰性气体 2/3 · 充气"));
        Assert.Equal((10, 10), EtaEstimator.PurgeCycle("05/05 结束 · 吹扫 10/10 · 排气"));
        Assert.Null(EtaEstimator.PurgeCycle("01/04 吹扫"));
        Assert.Null(EtaEstimator.PurgeCycle("x 4/3 · y"));
    }

    [Fact]
    public void Timed_FromHmiCountdown_EvenWhenStartUnknown()
    {
        var now = D0.AddMinutes(100);
        var e = EtaEstimator.Current(React, now, startUtc: null, firstSeenUtc: now, "03/04 反应 · 反应中 · 剩 12.5 分");
        Assert.Equal(EtaBasis.Timer, e.Basis);
        Assert.Equal(12.5 * 60, e.RemainS, 6);
        Assert.Equal(72 * 60, e.StepDurS, 6);                                 // 开始时刻按倒计时反推
        Assert.Equal(now.AddMinutes(-(72 - 12.5)), e.StepStart);
        var r = EtaEstimator.Apply(Running(2), now, null, now, "03/04 反应 · 反应中 · 剩 12.5 分");
        var pr = Prog(r, now);
        Assert.Equal(13, pr.StepRemainMin);
        Assert.Equal("第 3/4 步 · 剩 13 min", Progress.Text(pr));
        Assert.Equal("当前 · 剩 13 min", Progress.StepMark(r, 2, pr));
        // 时间往前走 5 min（两帧之间 Progress 自己倒计时）
        Assert.Equal(8, Prog(r, now.AddMinutes(5)).StepRemainMin);
        // 没有消息时按记下的开始时刻
        var e2 = EtaEstimator.Current(React, now, now.AddMinutes(-60), null, null);
        Assert.Equal(12 * 60, e2.RemainS, 6);
        Assert.Equal(EtaBasis.Timer, e2.Basis);
        // 什么都不知道：按时长，开始 = 第一次看到
        var e3 = EtaEstimator.Current(React, now, null, now.AddMinutes(-1), null);
        Assert.Equal(EtaBasis.Unknown, e3.Basis);
        Assert.Equal(now.AddMinutes(-1), e3.StepStart);
    }

    [Fact]
    public void Heat_RampEstimate_ThenBalanceCountdown()
    {
        var now = D0.AddMinutes(5);
        // 80 ℃ → 120 ℃，默认 3 ℃/min：(40 − 2) / 3 + 稳定 1 + 平衡 10 = 23.67 min
        var a = EtaEstimator.Current(Heat, now, D0, null, "01/04 升温 · 升温 80.0℃ → 120℃", new EtaReading(TempC: 80));
        Assert.Equal(EtaBasis.Estimate, a.Basis);
        Assert.Equal(((40 - 2) / 3.0 + 1 + 10) * 60, a.RemainS, 6);
        Assert.Equal(5 * 60 + a.RemainS, a.StepDurS, 6);
        // 实测 6 ℃/min（朝目标方向）优先
        var b = EtaEstimator.Current(Heat, now, D0, null, null, new EtaReading(TempC: 80, TempRate: 6));
        Assert.Equal(((40 - 2) / 6.0 + 1 + 10) * 60, b.RemainS, 6);
        // 实测速率方向不对（在降）或太小：用默认的
        Assert.Equal(a.RemainS, EtaEstimator.Current(Heat, now, D0, null, null, new EtaReading(TempC: 80, TempRate: -2)).RemainS, 6);
        Assert.Equal(a.RemainS, EtaEstimator.Current(Heat, now, D0, null, null, new EtaReading(TempC: 80, TempRate: 0.05)).RemainS, 6);
        // 到位（≤ 2 ℃）但还没开始平衡：剩平衡时长
        Assert.Equal(10 * 60, EtaEstimator.Current(Heat, now, D0, null, null, new EtaReading(TempC: 119)).RemainS, 6);
        // 平衡段：HMI 倒计时
        var c = EtaEstimator.Current(Heat, now, D0, null, "01/04 升温 · 已到 120℃ · 平衡剩 3 分", new EtaReading(TempC: 120));
        Assert.Equal(EtaBasis.Timer, c.Basis);
        Assert.Equal(180, c.RemainS, 6);
        Assert.Equal(D0, c.StepStart);
        // 速率设置来自 HMI 的逼近设置
        var rates = EtaRates.FromSettings(null);
        Assert.Same(EtaRates.Default, rates);
    }

    [Fact]
    public void CurveHeat_UsesProfile()
    {
        var curve = Heat with { HeatCurve = true };
        var pf = new ProfileSnap(true, 3, Array.Empty<ProfileSegmentSnap>(), 12.5, 40, false, "");
        var e = EtaEstimator.Current(curve, D0, null, null, null, new EtaReading(TempC: 90, Profile: pf));
        Assert.Equal(EtaBasis.Profile, e.Basis);
        Assert.Equal((40 - 12.5) * 60, e.RemainS, 6);
        Assert.Equal(40 * 60, e.StepDurS, 6);
    }

    [Fact]
    public void Press_EstimateThenOverdue_WaitsForCondition()
    {
        var now = D0.AddMinutes(30);
        var e = EtaEstimator.Current(Press, now, now.AddMinutes(-1), null, "02/04 充压 · 充压 0.100 / 0.350 MPa", new EtaReading(PressureMPa: 0.1));
        Assert.Equal(EtaBasis.Estimate, e.Basis);
        Assert.Equal(0.25 / 0.5 * 60, e.RemainS, 6);
        // 已到压力：剩余 0 → Progress 写「等条件」（充压的结束条件是压力）
        var r = EtaEstimator.Apply(Running(1), now, now.AddMinutes(-3), null, null, new EtaReading(PressureMPa: 0.36));
        var pr = Prog(r, now, state: StateKey.Press);
        Assert.True(pr.Overdue);
        Assert.Equal("第 2/4 步 · 等压力 ≥ 0.350 MPa", Progress.Text(pr));
        Assert.Equal("2/4 · → 0.350 MPa", Progress.Text(pr, shortForm: true));
        Assert.Equal("当前 · 等条件", Progress.StepMark(r, 1, pr));
    }

    [Fact]
    public void End_Cooling_ThenOverdue()
    {
        var now = D0.AddMinutes(200);
        var a = EtaEstimator.Current(End, now, now.AddMinutes(-2), null, null, new EtaReading(TempC: 90, TempRate: -2));
        Assert.Equal((90 - 50) / 2.0 * 60, a.RemainS, 6);
        var b = EtaEstimator.Current(End, now, now.AddMinutes(-2), null, null, new EtaReading(TempC: 90));
        Assert.Equal((90 - 50) / 1.0 * 60, b.RemainS, 6);
        var r = EtaEstimator.Apply(Running(3), now, now.AddMinutes(-40), null, null, new EtaReading(TempC: 49));
        var pr = Prog(r, now, state: StateKey.Cool);
        Assert.True(pr.Overdue);
        Assert.Equal("第 4/4 步 · 等温度 < 50 ℃", Progress.Text(pr));
    }

    [Fact]
    public void Plan_FillsConditionSteps_TotalRemain()
    {
        var plan = EtaEstimator.Plan(Steps, startTempC: 25);
        Assert.Equal(Math.Round((95 - 2) / 3.0 + 1 + 10, 1), plan[0].EstMin);   // 25 → 120 ℃
        Assert.Equal(Math.Round(Math.Max(0.5, 0.35 / 0.5), 1), plan[1].EstMin);
        Assert.Equal(72, plan[2].EstMin);                                        // 定时的不变
        Assert.Equal(Math.Round((120 - 50) / 1.0 + 1, 1), plan[3].EstMin);       // 结束：降温 + 收尾
        // 反应中、剩 12.5 min：全程剩余 = 13（当前，向上取整前 12.5）+ 结束步骤 71 min
        var now = D0.AddMinutes(100);
        var r = EtaEstimator.Apply(Running(2), now, null, now, "03/04 反应 · 反应中 · 剩 12.5 分", new EtaReading(TempC: 120, PressureMPa: 0.35));
        var pr = Prog(r, now);
        Assert.Equal((int)Math.Ceiling((12.5 * 60 + 71 * 60) / 60), pr.TotalRemainMin);
    }

    [Fact]
    public void Offline_ProgressFrozenAtDataAt()
    {
        var at = D0.AddMinutes(100);
        var r = EtaEstimator.Apply(Running(2), at, null, at, "03/04 反应 · 反应中 · 剩 12.5 分");
        var a = Prog(r, at.AddMinutes(10), online: false, dataAtUtc: at);
        var b = Prog(r, at.AddMinutes(30), online: false, dataAtUtc: at);
        Assert.Equal(13, a.StepRemainMin);
        Assert.Equal(13, b.StepRemainMin);
        Assert.Equal("离线 · 停在第 3/4 步", Progress.Text(a));
    }

    [Fact]
    public void ObservedRate_LeastSquaresSlope()
    {
        var h = new ChannelHistory();
        for (int i = 0; i <= 30; i++) h.Append(D0.AddSeconds(i * 10), 80 + i * 10 / 60.0 * 4, 0.1);   // 4 ℃/min
        Assert.Equal(4, EtaEstimator.ObservedRate(h, HistoryField.TempC, D0.AddSeconds(300))!.Value, 6);
        Assert.Equal(0, EtaEstimator.ObservedRate(h, HistoryField.PressureMPa, D0.AddSeconds(300))!.Value, 6);
        Assert.Null(EtaEstimator.ObservedRate(h, HistoryField.SpC, D0.AddSeconds(300)));             // 没有值
        Assert.Null(EtaEstimator.ObservedRate(h, HistoryField.TempC, D0.AddSeconds(20)));             // 点不够
        Assert.Null(EtaEstimator.ObservedRate(null, HistoryField.TempC, D0));
    }
}
