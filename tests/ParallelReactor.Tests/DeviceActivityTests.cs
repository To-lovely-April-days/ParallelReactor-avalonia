using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>
/// v0.8.1 设备活动同步（ACT-SPEC §1、§2）：泄漏测试各阶段的快照字段、各路状态码、容积标定等加水、搅拌电机故障报警只记一次，
/// 以及新记录的 JSON 形状与往返。LeakViewModel / CalibViewModel 在流程节点调用的就是这里的方法，SnapshotBuilder 调 ToSnap。
/// </summary>
public class DeviceActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(8));

    private static LeakActivity Started(params int[] ids)
    {
        var a = new LeakActivity();
        a.Begin(T0, "触屏", ids.Length > 0 ? ids : new[] { 1, 2, 3, 4 }, 50, 2, 10, 30);
        return a;
    }

    /// <summary>面板格子：RV1–4 参加，RV5–8 不测。</summary>
    private static List<(int Id, string Status, string Detail, double Pressure)> Cells(params (int Id, string Status, string Detail, double P)[] picked)
    {
        var list = picked.Select(c => (c.Id, c.Status, c.Detail, c.P)).ToList();
        for (int id = 1; id <= 8; id++)
            if (list.All(c => c.Id != id)) list.Add((id, "skip", "不测", 0.4));
        return list;
    }

    // ============ 泄漏测试 ============

    [Fact]
    public void Leak_NeverRun_OnlyLegacyFields()
    {
        var snap = new LeakActivity().ToSnap(T0, false, 0, "", null, Array.Empty<(int, string, string, double)>());
        Assert.Null(snap.Stage);
        Assert.Null(snap.Channels);
        Assert.Null(snap.Vented);
        Assert.False(snap.Done);
        Assert.False(snap.Aborted);
    }

    [Fact]
    public void Leak_Prep_StartFieldsAndTotalEstimate()
    {
        var a = Started();
        var snap = a.ToSnap(T0.AddSeconds(4), true, 0, "关闭全部阀门，读取初始压力…", "leak-20261119-090000",
            Cells((1, "", "…", 0.2), (2, "", "…", 0.2), (3, "", "…", 0.2), (4, "", "…", 0.2)));
        Assert.Equal(LeakStageCodes.Prep, snap.Stage);
        Assert.Null(snap.StageLeftSec);
        // 剩余 = prep 余 6 s + 充压 120 + 总管泄压 10 + 平衡 600 + 测量 1800 + 结束泄压 15
        Assert.Equal(6 + 120 + 10 + 600 + 1800 + 15, snap.TotalLeftSec);
        Assert.Equal(T0.UtcDateTime, snap.StartedAt);
        Assert.Equal(DateTimeKind.Utc, snap.StartedAt!.Value.Kind);
        Assert.Equal("触屏", snap.StartedBy);
        Assert.Equal(50, snap.TargetPsi);
        Assert.Equal(2, snap.ThresholdPsiH);
        Assert.Equal("关闭全部阀门，读取初始压力…", snap.Phase);
        Assert.True(snap.Running);
        Assert.False(snap.Done);
        Assert.Equal(8, snap.Channels!.Length);
        Assert.Equal(new[] { "testing", "testing", "testing", "testing", "untested", "untested", "untested", "untested" }, snap.Channels.Select(c => c.Status));
        Assert.Equal(Enumerable.Range(1, 8), snap.Channels.Select(c => c.Id));
        Assert.Null(snap.Channels[5].PressurePsi);   // 没参加的不给压力
    }

    [Fact]
    public void Leak_Fill_ChannelStatuses_TooHighAndFillFail()
    {
        var a = Started();
        var t = T0.AddSeconds(8);
        a.Enter(LeakStageCodes.Fill, t);
        var cells = Cells((1, "", "充压 60%", 30.12345), (2, "skip", "初始压力过高", 80), (3, "bad", "充压不上", 12), (4, "bad", "充压超时", 20));
        var snap = a.ToSnap(t.AddSeconds(30), true, 5, "充压中 · 1/3 路完成", null, cells);
        Assert.Equal(LeakStageCodes.Fill, snap.Stage);
        Assert.Null(snap.StageLeftSec);
        Assert.Equal(90 + 10 + 600 + 1800 + 15, snap.TotalLeftSec);
        Assert.Equal(new[] { "testing", "tooHigh", "fillFail", "fillFail", "untested" }, snap.Channels!.Take(5).Select(c => c.Status));
        Assert.Equal("充压 60%", snap.Channels![0].Detail);   // testing 原样用格子上的文字
        Assert.Equal(30.123, snap.Channels[0].PressurePsi);
        Assert.Equal(80, snap.Channels[1].PressurePsi);
        Assert.Null(snap.Channels[0].RatePsiH);
        // 充压超过估计时长：剩余不报 0
        Assert.Equal(LeakActivity.FillMinLeftSec + 10 + 600 + 1800 + 15, a.TotalLeftSec(t.AddSeconds(300)));
    }

    [Fact]
    public void Leak_LineVent_Settle_Measure_Vent_StageCodesAndTimes()
    {
        var a = Started();
        var t = T0.AddMinutes(2);
        a.Enter(LeakStageCodes.LineVent, t);
        Assert.Equal(LeakStageCodes.LineVent, a.ToSnap(t, true, 10, "总管泄压…", null, Cells()).Stage);
        Assert.Equal(10 + 600 + 1800 + 15, a.TotalLeftSec(t));

        a.Enter(LeakStageCodes.Settle, t, 600);
        var s = a.ToSnap(t.AddSeconds(100), true, 16, "平衡中 · 剩余 08:20", null, Cells((1, "", "平衡中", 50)));
        Assert.Equal(LeakStageCodes.Settle, s.Stage);
        Assert.Equal(500, s.StageLeftSec);
        Assert.Equal(500 + 1800 + 15, s.TotalLeftSec);
        Assert.Equal("testing", s.Channels![0].Status);
        Assert.Equal("平衡中", s.Channels[0].Detail);
        Assert.Equal(0, a.StageLeftSec(t.AddSeconds(700)));   // 平衡超时（最后一次采样）不报负数

        var tm = t.AddSeconds(600);
        a.Enter(LeakStageCodes.Measure, tm, 1800);
        var m1 = a.ToSnap(tm.AddSeconds(60), true, 37, "测量中 · 剩余 29:00", null, Cells((1, "", "-0.01 psi", 49.9)));
        Assert.Equal(LeakStageCodes.Measure, m1.Stage);
        Assert.Equal(1740, m1.StageLeftSec);
        Assert.Equal(1740 + 15, m1.TotalLeftSec);
        Assert.Null(m1.Channels![0].RatePsiH);   // 前 3 分钟还没有速率
        a.SetRate(1, 0.42);
        var m2 = a.ToSnap(tm.AddSeconds(300), true, 45, "测量中", null, Cells((1, "", "≈ 0.42 psi/hr", 49.8)));
        Assert.Equal(0.42, m2.Channels![0].RatePsiH);
        Assert.Equal("testing", m2.Channels[0].Status);

        a.Enter(LeakStageCodes.Vent, tm.AddSeconds(1800));
        var v = a.ToSnap(tm.AddSeconds(1805), true, 97, "尝试经排空阀泄压…", null, Cells((1, "ok", "0.42 psi/hr", 49.8)));
        Assert.Equal(LeakStageCodes.Vent, v.Stage);
        Assert.Null(v.StageLeftSec);
        Assert.Equal(10, v.TotalLeftSec);
        Assert.Equal("pass", v.Channels![0].Status);
    }

    [Fact]
    public void Leak_Done_NotVented_ResultsPersistUntilNextStart()
    {
        var a = Started(1, 2, 3);
        a.Enter(LeakStageCodes.Vent, T0.AddMinutes(43));
        a.SetRate(1, 0.3);
        a.SetRate(2, 3.1);
        var end = T0.AddMinutes(44);
        a.End(end, vented: false, aborted: false, fail: null, failed: true, progress: 100);
        var cells = Cells((1, "ok", "0.30 psi/hr", 49.5), (2, "bad", "3.10 psi/hr", 47.2), (3, "bad", "充压不上", 5));

        // 触屏重新打开面板：进度清 0、Done 清掉，但快照仍报这次的结果
        var snap = a.ToSnap(end.AddMinutes(5), false, 0, "", null, cells);
        Assert.Equal(LeakStageCodes.Done, snap.Stage);
        Assert.True(snap.Done);
        Assert.True(snap.Failed);
        Assert.False(snap.Running);
        Assert.Null(snap.Phase);
        Assert.Equal(100, snap.Progress);
        Assert.False(snap.Vented);
        Assert.False(snap.Aborted);
        Assert.Null(snap.FailReason);
        Assert.Equal(end.UtcDateTime, snap.FinishedAt);
        Assert.Null(snap.StageLeftSec);
        Assert.Null(snap.TotalLeftSec);
        Assert.Equal(new[] { "pass", "fail", "fillFail", "untested" }, snap.Channels!.Take(4).Select(c => c.Status));
        Assert.Equal(0.3, snap.Channels![0].RatePsiH);
        Assert.Equal(3.1, snap.Channels[1].RatePsiH);
        Assert.Equal(47.2, snap.Channels[1].PressurePsi);   // 仍有压力的通道（vented=false 时 PC 列出）

        // 下一次开始：清空
        a.Begin(end.AddMinutes(10), "远程（PC-1）", new[] { 5 }, 40, 2, 10, 30);
        var next = a.ToSnap(end.AddMinutes(10), true, 0, "", null, Cells((5, "", "…", 0)));
        Assert.False(next.Done);
        Assert.Null(next.Vented);
        Assert.Null(next.FinishedAt);
        Assert.Equal("远程（PC-1）", next.StartedBy);
        Assert.Null(next.Channels![0].RatePsiH);
        Assert.Equal("untested", next.Channels[0].Status);
        Assert.Equal("testing", next.Channels[4].Status);
    }

    [Fact]
    public void Leak_Aborted_UnfinishedCellsUntested()
    {
        var a = Started(1, 2);
        a.Enter(LeakStageCodes.Measure, T0.AddMinutes(12), 1800);
        a.SetRate(1, 0.5);
        a.End(T0.AddMinutes(20), vented: null, aborted: true, fail: null, failed: true, progress: 50);
        var snap = a.ToSnap(T0.AddMinutes(20), false, 50, "已中止", null, Cells((1, "", "未完成", 49), (2, "", "未完成", 49)));
        Assert.Equal(LeakStageCodes.Done, snap.Stage);
        Assert.True(snap.Aborted);
        Assert.True(snap.Failed);
        Assert.Null(snap.Vented);
        Assert.Equal(new[] { "untested", "untested" }, snap.Channels!.Take(2).Select(c => c.Status));
        Assert.Equal("未完成", snap.Channels![0].Detail);
        Assert.Equal(49, snap.Channels[0].PressurePsi);   // 参加了：仍给压力（釜里可能还有测试压力）
    }

    [Fact]
    public void Leak_Failed_CarriesReason()
    {
        var a = Started(1);
        a.End(T0.AddSeconds(5), vented: null, aborted: false, fail: "所选通道初始压力均高于测试压力，请先泄压", failed: true, progress: 0);
        var snap = a.ToSnap(T0.AddSeconds(6), false, 0, "", null, Cells((1, "skip", "初始压力过高", 80)));
        Assert.Equal("所选通道初始压力均高于测试压力，请先泄压", snap.FailReason);
        Assert.Equal("tooHigh", snap.Channels![0].Status);
        Assert.False(snap.Aborted);
    }

    [Theory]
    [InlineData(false, false, "skip", "不测", "untested")]
    [InlineData(false, false, "skip", "运行中", "untested")]
    [InlineData(true, false, "", "充压 40%", "testing")]
    [InlineData(true, false, "", "已到压", "testing")]
    [InlineData(true, false, "skip", "初始压力过高", "tooHigh")]
    [InlineData(true, false, "bad", "充压不上", "fillFail")]
    [InlineData(true, false, "bad", "充压超时", "fillFail")]
    [InlineData(true, true, "bad", "3.10 psi/hr", "fail")]
    [InlineData(true, true, "ok", "0.20 psi/hr", "pass")]
    [InlineData(true, true, "", "未完成", "untested")]
    public void Leak_StatusOf(bool picked, bool finished, string status, string detail, string expect)
        => Assert.Equal(expect, LeakActivity.StatusOf(picked, finished, status, detail));

    // ============ 容积标定 ============

    [Fact]
    public void Calib_NeverRun_IsNull()
        => Assert.Null(new CalibActivity().ToSnap(false, 0, "", 5));

    [Fact]
    public void Calib_TwoRuns_WaitWater_ThenDone()
    {
        var a = new CalibActivity();
        a.Begin(T0, 4, withWater: false, waterMl: 5, twoRuns: true, startedBy: "触屏");
        var first = a.ToSnap(true, 50, "第 3/6 次 · 总管充气", 5)!;
        Assert.Equal(CalibStageCodes.First, first.Stage);
        Assert.True(first.Running);
        Assert.False(first.WaitingOperator);
        Assert.Equal(4, first.Channel);
        Assert.Equal(5, first.WaterMl);
        Assert.Equal(T0.UtcDateTime, first.StartedAt);
        Assert.Equal("触屏", first.StartedBy);

        var tw = T0.AddMinutes(3);
        a.AwaitWater(tw, null, "第一遍完成（空釜）：比值 0.95");
        // 面板关掉也一样：等加水，加水体积按面板上当前的值（还可以改）
        var wait = a.ToSnap(false, 100, "等待加水", 4.5)!;
        Assert.Equal(CalibStageCodes.WaitWater, wait.Stage);
        Assert.True(wait.WaitingOperator);
        Assert.Equal(tw.UtcDateTime, wait.WaitingSince);
        Assert.Equal(4.5, wait.WaterMl);
        Assert.Null(wait.FirstPassMl);
        Assert.False(wait.Running);
        Assert.False(wait.Done);
        Assert.False(wait.Failed);

        a.Begin(T0.AddMinutes(10), 4, withWater: true, waterMl: 4.5, twoRuns: true, startedBy: "触屏");
        var second = a.ToSnap(true, 20, "第 1/6 次 · 总管充气", 9)!;
        Assert.Equal(CalibStageCodes.Second, second.Stage);
        Assert.False(second.WaitingOperator);
        Assert.Null(second.WaitingSince);
        Assert.Equal(T0.UtcDateTime, second.StartedAt);   // 开始时刻沿用第一遍
        Assert.Equal(4.5, second.WaterMl);                 // 第二遍起报实际加入的量

        a.Complete(10.62, "RV4 有效容积 10.6 mL：比值 0.95", 100, "标定完成");
        // 面板重新打开会清掉进度与文字，快照仍报结果
        var done = a.ToSnap(false, 0, "", 5)!;
        Assert.Equal(CalibStageCodes.Done, done.Stage);
        Assert.True(done.Done);
        Assert.False(done.Failed);
        Assert.Equal(10.62, done.ResultMl);
        Assert.Equal(100, done.Progress);
        Assert.Equal("标定完成", done.PhaseText);
        Assert.Equal("RV4 有效容积 10.6 mL：比值 0.95", done.ResultText);
    }

    [Fact]
    public void Calib_OneRun_ResultIsFirstPass_NoWater()
    {
        var a = new CalibActivity();
        a.Begin(T0, 2, withWater: false, waterMl: 5, twoRuns: false, startedBy: "触屏");
        Assert.Null(a.ToSnap(true, 10, "", 5)!.WaterMl);
        a.Complete(9.8, "RV2 有效容积 9.8 mL：…", 100, "标定完成");
        var s = a.ToSnap(false, 100, "标定完成", 5)!;
        Assert.Equal(9.8, s.ResultMl);
        Assert.Equal(9.8, s.FirstPassMl);
        Assert.True(s.Done);
    }

    [Fact]
    public void Calib_Failed_ThenDiscard_IsNull()
    {
        var a = new CalibActivity();
        a.Begin(T0, 1, withWater: false, waterMl: 5, twoRuns: true, startedBy: "触屏");
        a.Fail("标定已中止：已按要求中止，阀门已全关。", 33, "标定已中止");
        var s = a.ToSnap(false, 0, "", 5)!;
        Assert.Equal(CalibStageCodes.Failed, s.Stage);
        Assert.True(s.Failed);
        Assert.False(s.Done);
        Assert.False(s.WaitingOperator);
        Assert.Equal(33, s.Progress);
        a.Clear();
        Assert.Null(a.ToSnap(false, 0, "", 5));
    }

    // ============ 搅拌电机故障 ============

    [Fact]
    public void StirFault_RaisedOncePerOccurrence_ResolvedOnClear()
    {
        var w = new StirFaultWatch();
        var alarms = new AlarmIndex<string>();
        var log = new List<string>();
        void Poll(ushort code)
        {
            switch (w.Update(code))
            {
                case StirFaultWatch.Change.Raise:
                    var title = StirFaultWatch.Title(code);
                    log.Add("err " + title);
                    alarms.Raise(StirFaultWatch.Key, title);
                    break;
                case StirFaultWatch.Change.Clear:
                    log.Add($"info 解除 {alarms.TakeActive(StirFaultWatch.Key).Count}");
                    break;
            }
        }

        Poll(0); Poll(0);
        Assert.Empty(log);
        Poll(0x80); Poll(0x80); Poll(0x80);   // 轮询多次：只记一次
        Assert.Equal(new[] { "err 搅拌电机故障 · 代码 0x80 · 在触屏清除" }, log);
        Assert.Equal(1, alarms.Count(StirFaultWatch.Key));
        Poll(0);
        Assert.Equal("info 解除 1", log[^1]);
        Assert.Equal(0, alarms.Count(StirFaultWatch.Key));
        Poll(0);
        Assert.Equal(2, log.Count);
        Poll(0x01);   // 再次故障：新的一次
        Assert.Equal("err 搅拌电机故障 · 代码 0x1 · 在触屏清除", log[^1]);
        Assert.Equal(0x01, w.Code);
    }

    // ============ JSON ============

    private static TelemetrySnapshot FrameWith(LeakSnap leak, CalibSnap? calib)
        => TelemetrySnapshotJsonTests.Sample().Frame with { LeakTest = leak, Calib = calib };

    private static (LeakSnap Leak, CalibSnap Calib) SampleActivity()
    {
        var a = Started(1, 2, 3, 4);
        a.Enter(LeakStageCodes.Measure, T0.AddMinutes(12), 1800);
        a.SetRate(1, 0.42);
        var leak = a.ToSnap(T0.AddMinutes(20), true, 52.3, "测量中 · 剩余 22:00", "leak-20261119-090000",
            Cells((1, "", "≈ 0.42 psi/hr", 49.8), (2, "skip", "初始压力过高", 80), (3, "bad", "充压不上", 5), (4, "", "-0.01 psi", 50)));
        var c = new CalibActivity();
        c.Begin(T0, 6, false, 5, true, "触屏");
        c.AwaitWater(T0.AddMinutes(3), null, "第一遍完成（空釜）：比值 0.95");
        return (leak, c.ToSnap(false, 100, "等待加水", 5)!);
    }

    [Fact]
    public void Json_LeakAndCalib_Shape()
    {
        var (leak, calib) = SampleActivity();
        using var doc = JsonDocument.Parse(ApiJson.Serialize(FrameWith(leak, calib)));
        var lt = doc.RootElement.GetProperty("leakTest");
        Assert.Equal("measure", lt.GetProperty("stage").GetString());
        Assert.Equal(1320, lt.GetProperty("stageLeftSec").GetDouble());
        Assert.Equal("2026-11-19T01:00:00Z", lt.GetProperty("startedAt").GetString());
        Assert.Equal(JsonValueKind.Null, lt.GetProperty("vented").ValueKind);
        var ch = lt.GetProperty("channels");
        Assert.Equal(8, ch.GetArrayLength());
        Assert.Equal(new[] { "id", "status", "pressurePsi", "ratePsiH", "detail" }, ch[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("tooHigh", ch[1].GetProperty("status").GetString());
        Assert.Equal(0.42, ch[0].GetProperty("ratePsiH").GetDouble());

        var cb = doc.RootElement.GetProperty("calib");
        Assert.Equal(new[]
        {
            "running", "channel", "stage", "waitingOperator", "waterMl", "firstPassMl", "resultMl", "progress", "phaseText",
            "startedAt", "waitingSince", "startedBy", "done", "failed", "resultText",
        }, cb.EnumerateObject().Select(p => p.Name));
        Assert.Equal("waitWater", cb.GetProperty("stage").GetString());
        Assert.True(cb.GetProperty("waitingOperator").GetBoolean());
        Assert.Equal("2026-11-19T01:03:00Z", cb.GetProperty("waitingSince").GetString());
    }

    [Fact]
    public void Json_LeakAndCalib_RoundTrip()
    {
        var (leak, calib) = SampleActivity();
        var back = ApiJson.Deserialize<TelemetrySnapshot>(ApiJson.Serialize(FrameWith(leak, calib)))!;
        var bl = back.LeakTest;
        Assert.Equal(leak with { Channels = null }, bl with { Channels = null });   // 数组按值另比
        Assert.Equal(leak.Channels!, bl.Channels!);
        Assert.Equal(calib, back.Calib);
    }

    [Fact]
    public void Json_OldFrame_WithoutNewFields_Deserializes()
    {
        // 旧 HMI（v0.8 及以前）的帧：leakTest 只有 6 个字段、没有 calib
        var old = ApiJson.Serialize(TelemetrySnapshotJsonTests.Sample().Frame);
        using var doc = JsonDocument.Parse(old);
        var dict = doc.RootElement.EnumerateObject().Where(p => p.Name != "calib")
            .ToDictionary(p => p.Name, p => p.Name == "leakTest"
                ? JsonDocument.Parse("{\"running\":true,\"progress\":12,\"phase\":\"平衡中\",\"runId\":null,\"done\":false,\"failed\":false}").RootElement
                : p.Value.Clone());
        var back = ApiJson.Deserialize<TelemetrySnapshot>(JsonSerializer.Serialize(dict))!;
        Assert.Null(back.Calib);
        Assert.Null(back.LeakTest.Stage);
        Assert.Null(back.LeakTest.Channels);
        Assert.False(back.LeakTest.Aborted);
        Assert.True(back.LeakTest.Running);
    }
}
