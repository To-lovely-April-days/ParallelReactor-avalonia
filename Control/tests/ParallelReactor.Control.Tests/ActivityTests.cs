using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Activity;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// v0.8.1 设备活动同步（ACT-SPEC §1 / §3）的纯逻辑：快照 → 活动（阶段码、进度、各路状态、中止后的「未完成」）、文字（剩余时间、结果、带压提示、
/// 设备栏行、状态栏一项、胶囊）、圆环的段、自动展开（开始 / 等人操作 / 结束）与几台同时的摆法、全局标记（气路占用、手动阀控、搅拌故障、急停分项、自整定）。
/// </summary>
public class ActivityTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 14, 22, 47, DateTimeKind.Local);
    private static readonly DateTime Now = new(2026, 11, 15, 14, 30, 7, DateTimeKind.Local);

    private static LeakChannelSnap Ch(int n, string st, double? psi = 49.9, double? rate = null, string detail = "平衡中") => new(n, st, psi, rate, detail);

    private static LeakChannelSnap[] Run4() => new[] { Ch(1, "testing"), Ch(2, "testing", 49.8), Ch(3, "testing"), Ch(4, "testing", 49.7),
        Ch(5, "untested", null, null, "不测"), Ch(6, "untested", null, null, "不测"), Ch(7, "untested", null, null, "不测"), Ch(8, "untested", null, null, "运行中") };

    private static LeakSnap Running(string stage = "settle", double progress = 16.4, double? stageLeft = 320, double? total = 2240, DateTime? at = null) =>
        new(true, progress, "平衡中 · 剩余 05:20", "leak-1", false, false, stage, stageLeft, total, (at ?? T0).ToUniversalTime(), "触屏", 50, 0.5, Run4());

    private static LeakSnap Done(LeakChannelSnap[] chs, bool? vented, bool aborted = false, string? fail = null, DateTime? at = null) =>
        new(false, aborted ? 22 : 100, null, null, true, aborted || chs.Any(c => c.Status is "fail" or "fillFail"), "done", null, null, (at ?? T0).ToUniversalTime(), "触屏", 50, 0.5, chs,
            vented, aborted, fail, Now.AddMinutes(-2).ToUniversalTime());

    private static CalibSnap Calib(string stage, bool wait, double? water = 5, double? first = null, double? result = null, bool done = false, bool failed = false) =>
        new(!done && !failed, 3, stage, wait, water, first, result, wait ? 100 : 45, wait ? "等待加水" : "第一遍", T0.ToUniversalTime(),
            wait ? Now.AddSeconds(-192).ToUniversalTime() : null, "触屏", done, failed, done ? "完成：有效容积 10.82 mL" : failed ? "失败：压力不稳" : null);

    // ———————————————————— 快照 → 活动 ————————————————————

    [Theory]
    [InlineData("prep", 0, 0)]
    [InlineData("fill", 5, 0)]
    [InlineData("lineVent", 12, 0)]
    [InlineData("settle", 20, 1)]
    [InlineData("measure", 50, 2)]
    [InlineData("vent", 98, 3)]
    [InlineData("done", 100, 3)]
    [InlineData(null, 5, 0)]       // 旧 HMI 没有阶段码：按进度（充压 0–12、平衡 12–35、测量 35–97）
    [InlineData(null, 20, 1)]
    [InlineData(null, 60, 2)]
    [InlineData(null, 98, 3)]
    public void LeakStage_UsesStageCode(string? code, double progress, int want) => Assert.Equal(want, ActivityBuild.LeakStage(code, progress));

    [Fact]
    public void Leak_Running_ParsesChannelsAndTimes()
    {
        var a = ActivityBuild.Leak(Running())!;
        Assert.Equal(ActKind.Leak, a.Kind);
        Assert.Equal(ActPhase.Running, a.Phase);
        Assert.Equal(1, a.Stage);
        Assert.Equal(new[] { 1, 2, 3, 4 }, a.Chs);
        Assert.Equal(new[] { 5, 6, 7, 8 }, a.Untested);
        Assert.Equal(16.4, a.Progress);           // 不乘 100
        Assert.Equal(T0, a.StartedAt);            // UTC → 本地
        Assert.Equal(DateTimeKind.Local, a.StartedAt!.Value.Kind);
        Assert.True(a.Live);
    }

    [Fact]
    public void Leak_NothingSinceBoot_IsNull()
    {
        // 本次开机还没做过泄漏测试：新字段都是 null、done = false（act-hmi-handover §6）
        Assert.Null(ActivityBuild.Leak(new LeakSnap(false, 0, null, null, false, false)));
        Assert.Null(ActivityBuild.Leak(null));
    }

    [Fact]
    public void Leak_Results_Phases()
    {
        var pass = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 43.5, .12), Ch(2, "pass", 43.5, .2) }, true))!;
        Assert.Equal(ActPhase.Passed, pass.Phase);
        Assert.Equal(3, pass.Stage);
        Assert.Equal(100, pass.Progress);
        var fail = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 43.5, .12), Ch(2, "fillFail", 21.6), Ch(3, "fail", 43.5, 2.04), Ch(4, "tooHigh", 43.5, null, "初始压力过高") }, false))!;
        Assert.Equal(ActPhase.Failed, fail.Phase);
        Assert.Equal((1, 2, 1), fail.Counts);
        var err = ActivityBuild.Leak(Done(new[] { Ch(1, "untested", 10, null, "未完成") }, null, fail: "没有通道充压成功"))!;
        Assert.Equal(ActPhase.Error, err.Phase);
    }

    [Fact]
    public void Leak_Aborted_PickedButUntested_AreUnfinished()
    {
        // 中止后参加了、没出结果的路：HMI 报 untested「未完成」——算参加的（写「未完成」），没参加的仍是「不测」
        var chs = new[] { Ch(1, "untested", 49.7, null, "未完成"), Ch(2, "untested", 49.6, null, "未完成"), Ch(3, "tooHigh", 80, null, "初始压力过高"),
            Ch(4, "untested", null, null, "不测"), Ch(5, "untested", null, null, "运行中") };
        var a = ActivityBuild.Leak(Done(chs, null, aborted: true))!;
        Assert.Equal(ActPhase.Aborted, a.Phase);
        Assert.Equal(new[] { 1, 2, 3 }, a.Chs);
        Assert.Equal(new[] { 4, 5 }, a.Untested);
        Assert.Equal("未完成", ActivityText.Chip(a, a.Channels[0]).Word);
        Assert.Equal("初始压力过高", ActivityText.Chip(a, a.Channels[2]).Word);
        Assert.Equal("RV4–5 不测", ActivityText.OffLine(a));
    }

    [Fact]
    public void Key_IsStableAcrossTheEnd()
    {
        // HMI 结束后 runId 变成 null：同一次测试的身份不能变（不然「结束」看成一次新的开始）
        var run = ActivityBuild.Leak(Running())!;
        var end = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 1, .1) }, true))!;
        Assert.Equal(run.Key, end.Key);
        var next = ActivityBuild.Leak(Running(at: T0.AddHours(1)))!;
        Assert.NotEqual(run.Key, next.Key);
    }

    [Fact]
    public void Calib_Phases()
    {
        var w = ActivityBuild.Calib(Calib("waitWater", true))!;
        Assert.Equal(ActPhase.Waiting, w.Phase);
        Assert.Equal(1, w.Stage);
        Assert.True(w.Waiting);
        Assert.Equal(192, ActivityText.WaitedSec(w, Now), 3);
        Assert.Equal(ActPhase.Running, ActivityBuild.Calib(Calib("first", false))!.Phase);
        Assert.Equal(2, ActivityBuild.Calib(Calib("second", false))!.Stage);
        Assert.Equal(ActPhase.Passed, ActivityBuild.Calib(Calib("done", false, result: 10.82, done: true))!.Phase);
        Assert.Equal(ActPhase.Error, ActivityBuild.Calib(Calib("failed", false, failed: true))!.Phase);
        Assert.Null(ActivityBuild.Calib(null));   // 「放弃第一遍」后 calib = null
    }

    [Fact]
    public void Pick_WaitingFirst_ThenLive_ThenLatest()
    {
        var leak = ActivityBuild.Leak(Running())!;
        var wait = ActivityBuild.Calib(Calib("waitWater", true))!;
        Assert.Same(wait, ActivityBuild.Pick(leak, wait));
        var calibDone = ActivityBuild.Calib(Calib("done", false, result: 10.8, done: true))!;
        Assert.Same(leak, ActivityBuild.Pick(leak, calibDone));
        var leakDone = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 1, .1) }, true, at: T0.AddHours(-1)))!;
        Assert.Same(calibDone, ActivityBuild.Pick(leakDone, calibDone));   // 都结束了：开始得晚的
    }

    // ———————————————————— 文字 ————————————————————

    [Theory]
    [InlineData(2240, "37:20")]
    [InlineData(320, "05:20")]
    [InlineData(59.6, "01:00")]
    [InlineData(0, "00:00")]
    [InlineData(-5, "00:00")]
    [InlineData(4500, "75:00")]
    public void Mmss(double sec, string want) => Assert.Equal(want, ActivityText.Mmss(sec));

    [Fact]
    public void Range_ConsecutiveOrList()
    {
        Assert.Equal("RV1–4", ActivityText.Range(new[] { 4, 2, 3, 1 }));
        Assert.Equal("RV1、RV3", ActivityText.Range(new[] { 3, 1 }));
        Assert.Equal("RV5", ActivityText.Range(new[] { 5 }));
        Assert.Equal("", ActivityText.Range(Array.Empty<int>()));
    }

    [Fact]
    public void RunningTexts()
    {
        var a = ActivityBuild.Leak(Running())!;
        Assert.Equal("1号机 · 泄漏测试", ActivityText.Title("1号机", a));
        Assert.Equal("RV1–4 · 测试压力 0.34 MPa（50 psi） · 触屏发起", ActivityText.HeadSub(a, "PC-测试工位1"));
        Assert.Equal("平衡", ActivityText.StageName(a));
        Assert.Equal(new DateTime(2026, 11, 15, 15, 7, 27, DateTimeKind.Local), ActivityText.EndAt(a, Now));
        Assert.Equal("平衡 · 剩余 37:20", ActivityText.Meta(a, Now));
        Assert.Equal((ActTone.Run, "f-timer", "泄漏测试 · 平衡 · 16 %"), ActivityText.DevLine(a));
        Assert.Equal("1号机 · 泄漏测试 · 平衡 · 16 %", ActivityText.Status("1号机", a, narrow: false));
        Assert.Equal("1号机 泄漏测试 16 %", ActivityText.Status("1号机", a, narrow: true));
        Assert.Equal("判据 ≤ 0.5 psi/h", ActivityText.Threshold(a));
        Assert.Equal("RV5–8 不测", ActivityText.OffLine(a));
        var chip = ActivityText.Chip(a, a.Channels[0]);
        Assert.Equal((ChipKind.Run, "0.344", "MPa", "平衡中"), (chip.Kind, chip.Value, chip.Unit, chip.Word));
    }

    [Theory]
    [InlineData("触屏", "触屏发起")]
    [InlineData("远程（PC-测试工位1）", "本机发起")]
    [InlineData("远程（PC-工位2）", "PC-工位2 发起")]
    [InlineData(null, "")]
    public void Source(string? by, string want)
    {
        var a = ActivityBuild.Leak(Running() with { StartedBy = by })!;
        Assert.Equal(want, ActivityText.Source(a, "PC-测试工位1"));
    }

    [Fact]
    public void ResultTexts_FailWithHold()
    {
        var a = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 43.5, .12), Ch(2, "fillFail", 21.6), Ch(3, "fail", 43.5, 2.04), Ch(4, "tooHigh", 43.5, null, "初始压力过高") }, false))!;
        Assert.Equal("2 路不通过", ActivityText.Verdict(a));
        Assert.Equal("1 路通过 · 1 路未测", ActivityText.VerdictSub(a));
        Assert.Equal("已结束 · 2 路不通过", ActivityText.Meta(a, Now));
        Assert.Equal((ActTone.Amb, "f-warn-f", "泄漏测试结束 · 2 路不通过"), ActivityText.DevLine(a));
        var w = ActivityText.HoldWarn(a);
        Assert.NotNull(w);
        Assert.Equal("釜内仍有测试压力，拆釜前请先泄压", w!.Value.Title);
        // 充压不上的 RV2 不算（样稿只列 RV1、RV3、RV4）
        Assert.Equal("泄压阶段压力没有降下来 · RV1、RV3、RV4 仍约 0.30 MPa · 在触屏上手动放空", w.Value.Body);
        Assert.Equal((ChipKind.Ok, "0.12", "psi/h", "通过"), Tuple(ActivityText.Chip(a, a.Channels[0])));
        Assert.Equal((ChipKind.No, "0.15", "MPa", "充压不上"), Tuple(ActivityText.Chip(a, a.Channels[1])));
        Assert.Equal((ChipKind.No, "2.04", "psi/h", "不通过"), Tuple(ActivityText.Chip(a, a.Channels[2])));
        Assert.Equal((ChipKind.Amb, "未测 · 先泄压", "", "初始压力过高"), Tuple(ActivityText.Chip(a, a.Channels[3])));
    }

    private static (ChipKind, string, string, string) Tuple(ActChip c) => (c.Kind, c.Value, c.Unit, c.Word);

    [Fact]
    public void ResultTexts_PassAndAbort()
    {
        var pass = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 0.5, .12), Ch(2, "pass", 0.4, .2) }, true))!;
        Assert.Equal("2 路全部通过", ActivityText.Verdict(pass));
        Assert.Null(ActivityText.HoldWarn(pass));
        Assert.Equal("RV1–2 · 14:28 完成 · 用时 5 min · 触屏发起", ActivityText.HeadSub(pass, null));
        var ab = ActivityBuild.Leak(Done(new[] { Ch(1, "untested", 49.7, null, "未完成") }, null, aborted: true))!;
        Assert.Equal("已中止", ActivityText.Verdict(ab));
        Assert.Equal("已中止 · 釜内带压", ActivityText.Meta(ab, Now));
        Assert.StartsWith("中止不放空 · RV1 约 0.34", ActivityText.HoldWarn(ab)!.Value.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void CalibTexts()
    {
        var w = ActivityBuild.Calib(Calib("waitWater", true))!;
        Assert.Equal("1号机 · 容积标定 · RV3", ActivityText.Title("1号机", w));
        Assert.Equal("第二遍 · 加水后再读一遍 · 触屏发起", ActivityText.HeadSub(w, null));
        Assert.Equal("等待触屏操作 · 已等 03:12", ActivityText.Meta(w, Now));
        Assert.Equal((ActTone.Amb, "f-warn-f", "容积标定 RV3 · 等待触屏操作"), ActivityText.DevLine(w));
        Assert.Equal("1号机 · 容积标定 RV3 · 等待触屏操作", ActivityText.Status("1号机", w, false));
        Assert.Equal(ActTone.Amb, ActivityText.Tone(w));
    }

    // ———————————————————— 圆环 ————————————————————

    [Fact]
    public void Ring_Running_WeightsAndFill()
    {
        var a = ActivityBuild.Leak(Running())!;
        var r = ActivityText.Ring(a, 10, 30);
        Assert.Equal(new[] { 160.0, 600, 1800, 120 }, r.Select(x => x.Weight));
        Assert.Equal(new[] { RingSegKind.Done, RingSegKind.Cur, RingSegKind.Next, RingSegKind.Next }, r.Select(x => x.Kind));
        Assert.Equal(1 - 320.0 / 600, r[1].Fill, 6);
    }

    [Fact]
    public void Ring_Results_OnePerChannel()
    {
        var a = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 1, .1), Ch(2, "fillFail", 1), Ch(3, "fail", 1, 3), Ch(4, "tooHigh", 80) }, false))!;
        Assert.Equal(new[] { RingSegKind.Ok, RingSegKind.No, RingSegKind.No, RingSegKind.Amb }, ActivityText.Ring(a, 10, 30).Select(x => x.Kind));
        var ab = ActivityBuild.Leak(Done(new[] { Ch(1, "untested", 49, null, "未完成") }, null, aborted: true))!;
        Assert.Equal(RingSegKind.Next, Assert.Single(ActivityText.Ring(ab, 10, 30)).Kind);
    }

    [Fact]
    public void Ring_CalibWaiting_IsAmber()
    {
        var w = ActivityBuild.Calib(Calib("waitWater", true))!;
        var r = ActivityText.Ring(w, 10, 30);
        Assert.Equal(new[] { RingSegKind.DoneSoft, RingSegKind.CurAmb, RingSegKind.Next, RingSegKind.Next }, r.Select(x => x.Kind));
        Assert.Equal(1, r[1].Fill);
    }

    // ———————————————————— 自动展开 · 几台同时 ————————————————————

    private static DeviceActivity LeakRun(DateTime? at = null) => ActivityBuild.Leak(Running(at: at))!;
    private static DeviceActivity LeakEnd(DateTime? at = null) => ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 1, .1) }, true, at: at))!;
    private static DeviceActivity CalibWait() => ActivityBuild.Calib(Calib("waitWater", true))!;
    private static DeviceActivity CalibFirst() => ActivityBuild.Calib(Calib("first", false))!;

    [Fact]
    public void Deck_StartWaitingFinished_AutoOpen()
    {
        var d = new ActivityDeck();
        Assert.Empty(d.Update(new (int, DeviceActivity?)[] { (1, null) }, Now));
        var c = d.Update(new (int, DeviceActivity?)[] { (1, LeakRun()) }, Now);
        Assert.Equal(ActTransition.Started, c[1]);
        Assert.Equal(1, d.Layout(Now).Open!.K);
        // 收起之后同一次测试不再弹
        d.Fold(1);
        Assert.Empty(d.Update(new (int, DeviceActivity?)[] { (1, LeakRun()) }, Now));
        Assert.Null(d.Layout(Now).Open);
        Assert.True(Assert.Single(d.Layout(Now).Pills).Folded);
        // 结束：再弹一次（结果）
        c = d.Update(new (int, DeviceActivity?)[] { (1, LeakEnd()) }, Now);
        Assert.Equal(ActTransition.Finished, c[1]);
        Assert.Equal(1, d.Layout(Now).Open!.K);
        // 关闭：这一次不再显示；下一次开始照常弹
        d.Close(1);
        Assert.Empty(d.Visible(Now));
        c = d.Update(new (int, DeviceActivity?)[] { (1, LeakRun(T0.AddHours(1))) }, Now);
        Assert.Equal(ActTransition.Started, c[1]);
        Assert.Single(d.Visible(Now));
    }

    [Fact]
    public void Deck_CalibStartsWaiting_AutoOpens()
    {
        var d = new ActivityDeck();
        d.Update(new (int, DeviceActivity?)[] { (2, CalibFirst()) }, Now);
        d.Fold(2);
        var c = d.Update(new (int, DeviceActivity?)[] { (2, CalibWait()) }, Now);
        Assert.Equal(ActTransition.Waiting, c[2]);
        Assert.Equal(2, d.Layout(Now).Open!.K);
    }

    [Fact]
    public void Deck_FirstSeenFinished_StaysFolded_AndOldOnesHide()
    {
        var d = new ActivityDeck();
        // 打开软件时看到的就是刚结束的结果：不弹，收在胶囊里
        Assert.Empty(d.Update(new (int, DeviceActivity?)[] { (1, LeakEnd()) }, Now));
        Assert.Null(d.Layout(Now).Open);
        Assert.Single(d.Layout(Now).Pills);
        // 很久以前结束、PC 又没看着它进行的：不显示
        var stale = ActivityBuild.Leak(Done(new[] { Ch(1, "pass", 1, .1) }, true, at: T0.AddHours(-3)) with { FinishedAt = Now.AddHours(-2).ToUniversalTime() })!;
        var o2 = new ActivityDeck();
        o2.Update(new (int, DeviceActivity?)[] { (1, stale) }, Now);
        Assert.Empty(o2.Visible(Now));
    }

    [Fact]
    public void Deck_Stack_WaitingOpensFirst_OthersArePills()
    {
        var d = new ActivityDeck();
        d.Update(new (int, DeviceActivity?)[] { (1, LeakRun()), (2, CalibWait()) }, Now);
        var (open, pills) = d.Layout(Now);
        Assert.Equal(2, open!.K);                       // 等人操作的优先
        Assert.Equal(new[] { 1 }, pills.Select(p => p.K));
        Assert.False(pills[0].Folded);
        // 点胶囊：换它展开，另一台变胶囊
        d.Expand(1);
        (open, pills) = d.Layout(Now);
        Assert.Equal(1, open!.K);
        Assert.Equal(new[] { 2 }, pills.Select(p => p.K));
        // 一台新开始的不抢等人操作的
        var d2 = new ActivityDeck();
        d2.Update(new (int, DeviceActivity?)[] { (1, null), (2, CalibWait()) }, Now);
        d2.Update(new (int, DeviceActivity?)[] { (1, LeakRun()), (2, CalibWait()) }, Now);
        Assert.Equal(2, d2.Layout(Now).Open!.K);
        // 全部收起：没有展开的，胶囊按设备号
        d2.SetAllFolded(true);
        Assert.Null(d2.Layout(Now).Open);
        Assert.Equal(new[] { 1, 2 }, d2.Layout(Now).Pills.Select(p => p.K));
    }

    // ———————————————————— 标记 ————————————————————

    private static TelemetrySnapshot Frame(GasSnap gas, StirSnap stir, EstopSnap estop, params ChannelSnap[] chs) =>
        new("telemetry", 1, Now.ToUniversalTime(), false, 0, new ModeSnap("remote", "PC"), estop, new CommSnap(true, true, true, true), chs, stir, gas,
            new ProgramSnap(false, null, "", -1, 0, "", ""), new ProgramsSnap(0, Array.Empty<int>(), null, Array.Empty<int>(), ""), new LeakSnap(false, 0, null, null, false, false),
            new SessionSnap(false, null, null, null));

    private static ChannelSnap Chan(int n, bool tuning, double sp) => new(n, true, "idle", null, false, false, null, 25, sp, null, null, true, tuning, null, "fixed",
        new ApproachSnap("off", null, null, ""), null, 0, null, 0, null, false, false, null);

    [Fact]
    public void Marks_FromFrame()
    {
        var f = Frame(new GasSnap(Array.Empty<GasInletSnap>(), false, true, "泄漏测试", "leak"),
            new StirSnap(false, 400, true, false, null, null, "锁轴/缺相（堵转）", 0x80, 1000),
            new EstopSnap(true, "ok", "ok", "failed", Now.AddSeconds(-15).ToUniversalTime(), true, false),
            Chan(5, true, 150), Chan(6, false, 25));
        var m = DeviceMarks.From(f);
        Assert.Equal("leak", m.GasOwner);
        Assert.True(m.GasByActivity);
        Assert.True(m.ManualValve);
        Assert.True(m.HasStirFault);
        Assert.Equal("0x80", m.StirCodeText);
        Assert.True(m.Tuning(5));
        Assert.False(m.Tuning(6));
        Assert.Equal(150, m.Autotune[0].SpC);
        Assert.True(m.Estop!.AnyUnconfirmed(Now));
        Assert.Equal(new[] { "阀门" }, m.Estop.UnconfirmedParts(Now));
        Assert.Equal((3, 4), m.Estop.Confirmed(Now));
        // 没有故障：fault = null、alarmCode = 0
        var ok = DeviceMarks.From(Frame(new GasSnap(Array.Empty<GasInletSnap>(), false, false, null, null), new StirSnap(true, 400, true, false, null, null, null, 0, 1000),
            new EstopSnap(false, "pending", "pending", "pending", null, false, false)));
        Assert.False(ok.HasStirFault);
        Assert.Null(ok.StirCodeText);
        Assert.Null(ok.Estop);   // 没急停时 pending 不算
    }

    [Fact]
    public void Estop_PendingCountsOnlyAfterGrace_NotConfiguredIsNa()
    {
        var since = Now.AddSeconds(-3);
        var p = new EstopParts(since, EstopPartState.Ok, EstopPartState.Pending, EstopPartState.NotConfigured);
        Assert.False(p.AnyUnconfirmed(Now));                    // 刚急停的几秒里 pending 是正常的
        Assert.True(p.AnyUnconfirmed(Now.AddSeconds(20)));      // 一直没确认
        Assert.Equal((2, 3), p.Confirmed(Now));                 // 程序 + 加热；阀门没配不算
        Assert.Equal("没有配置", p.Rows(Now)[3].Done);
    }

    // ———————————————————— 演示数据 ————————————————————

    [Fact]
    public void Demo_SeedLeak_MatchesMock()
    {
        var f = new DemoFleet(DemoParams.Parse("fusion=1&freeze=1"));
        var d = f.Device(1)!;
        f.SeedLeak(d, "run", "hmi");
        var a = ActivityBuild.Leak(d.LeakTest)!;
        Assert.Equal("平衡 · 剩余 37:20", ActivityText.Meta(a, f.Now));
        Assert.Equal("15:07", Fmt.Hm(ActivityText.EndAt(a, f.Now)));
        Assert.Contains(5, d.GasQueue);
        Assert.DoesNotContain(d.Alarms, x => x.Ch is 2 or 4);
        Assert.Equal("当前没有进行中的泄漏测试", f.AbortLeakDemo(f.Device(2)!));
        Assert.Equal("", f.AbortLeakDemo(d));
        var ab = ActivityBuild.Leak(d.LeakTest)!;
        Assert.Equal(ActPhase.Aborted, ab.Phase);
        Assert.Equal(new[] { 1, 2, 3, 4 }, ab.Chs);
    }

    [Fact]
    public void Demo_LeakCountsDown_AndFinishes()
    {
        var f = new DemoFleet(DemoParams.Parse("fusion=1"));
        var d = f.Device(1)!;
        f.StartLeakDemo(d, new[] { 1, 2 });
        var a = ActivityBuild.Leak(d.LeakTest)!;
        Assert.Equal(0, a.Stage);
        double total = a.TotalLeftSec!.Value;
        for (int i = 0; i < 200; i++) f.Tick();
        a = ActivityBuild.Leak(d.LeakTest)!;
        Assert.Equal(1, a.Stage);                      // 160 s 之后在平衡
        Assert.Equal(total - 200, a.TotalLeftSec!.Value, 3);
        for (int i = 0; i < (int)total; i++) f.Tick();
        a = ActivityBuild.Leak(d.LeakTest)!;
        Assert.Equal(ActPhase.Passed, a.Phase);
    }
}
