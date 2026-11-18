using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Records;
using ParallelReactor.Control.Core.Unattended;
using ParallelReactor.PerfTest.Model;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F8 的核心逻辑：无人值守（防睡眠的决定、报警声音的节奏）、断线补拉（CSV → 曲线采样、并进曲线缓存）、保压类判据跟着设备设置、
/// 性能测试计划按釜转速（上限 450）。系统调用（SetThreadExecutionState / caffeinate / PlaySound / afplay）在接口后面，这里用假的。
/// </summary>
public class F8UnattendedTests
{
    private sealed class FakeInhibitor : ISleepInhibitor
    {
        public bool Ok = true;
        public int Acquires, Releases;
        public bool Holding;
        public string Name => "假的";
        public string? LastError => Ok ? null : "拒绝";
        public bool Acquire() { Acquires++; if (Ok) Holding = true; return Ok; }
        public void Release() { Releases++; Holding = false; }
    }

    private sealed class FakeSound : IAlarmSound
    {
        public int Plays;
        public void Play() => Plays++;
    }

    // ———————————————————— 防睡眠 ————————————————————

    [Fact]
    public void SleepGuard_HoldsOnlyWhileEnabledAndRunning()
    {
        var os = new FakeInhibitor();
        var g = new SleepGuard(os);
        Assert.False(g.Update(true, false));
        Assert.Equal((false, 0), (g.Held, os.Acquires));
        Assert.True(g.Update(true, true));
        Assert.True(g.Held && os.Holding);
        // 每秒刷新：不重复申请
        for (int i = 0; i < 5; i++) g.Update(true, true);
        Assert.Equal(1, os.Acquires);
        // 全部结束 → 释放
        Assert.True(g.Update(true, false));
        Assert.Equal((false, 1, false), (g.Held, os.Releases, os.Holding));
        // 设置关掉 → 即使在跑也不阻止
        g.Update(true, true);
        Assert.True(g.Held);
        g.Update(false, true);
        Assert.Equal((false, false, 2), (g.Held, g.Wanted, os.Releases));
        g.Update(true, true);
        g.Dispose();
        Assert.False(os.Holding);
    }

    [Fact]
    public void SleepGuard_FailedRequest_RetriesAfterAMinute_AndReportsWhy()
    {
        var os = new FakeInhibitor { Ok = false };
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var g = new SleepGuard(os, () => now);
        g.Update(true, true);
        Assert.Equal((false, true, "拒绝"), (g.Held, g.Wanted, g.Error));
        now = now.AddSeconds(30);
        g.Update(true, true);
        Assert.Equal(1, os.Acquires);                 // 60 s 之内不再试
        now = now.AddSeconds(31);
        os.Ok = true;
        g.Update(true, true);
        Assert.Equal((2, true, (string?)null), (os.Acquires, g.Held, g.Error));
    }

    // ———————————————————— 报警声音 ————————————————————

    [Fact]
    public void AlarmBeeper_NewAlarmAtOnce_ThenEvery30s_UntilAcked()
    {
        var snd = new FakeSound();
        var b = new AlarmBeeper(snd);
        var t = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(b.Tick(t, true, Array.Empty<string>()));
        Assert.True(b.Tick(t, true, new[] { "1:a" }));            // 新报警：马上响
        Assert.False(b.Tick(t.AddSeconds(10), true, new[] { "1:a" }));
        Assert.False(b.Tick(t.AddSeconds(29), true, new[] { "1:a" }));
        Assert.True(b.Tick(t.AddSeconds(30), true, new[] { "1:a" })); // 30 s 一次
        Assert.True(b.Tick(t.AddSeconds(35), true, new[] { "1:a", "3:b" }));   // 又来一条新的：马上响
        Assert.False(b.Tick(t.AddSeconds(40), true, new[] { "3:b" }));          // 确认了一条：还有一条，按节奏
        Assert.False(b.Tick(t.AddSeconds(50), true, Array.Empty<string>()));    // 全部确认：停
        Assert.True(b.Tick(t.AddSeconds(51), true, new[] { "3:b" }));           // 确认后再出现算新的
        Assert.Equal(4, snd.Plays);
    }

    [Fact]
    public void AlarmBeeper_SoundOff_NeverPlays_TurningOnKeepsTheCadence()
    {
        var snd = new FakeSound();
        var b = new AlarmBeeper(snd);
        var t = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(b.Tick(t, false, new[] { "1:a" }));
        Assert.False(b.Tick(t.AddSeconds(40), false, new[] { "1:a" }));
        Assert.Equal(0, snd.Plays);
        Assert.True(b.Tick(t.AddSeconds(41), true, new[] { "1:a" }));    // 打开：已经超过 30 s 了，响
        Assert.False(b.Tick(t.AddSeconds(45), true, new[] { "1:a" }));
        Assert.Equal(1, snd.Plays);
    }

    // ———————————————————— 补拉 ————————————————————

    private static readonly DateTime U0 = new(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ChannelHistory_Merge_FillsTheGap_KeepsLiveFrames()
    {
        var h = new ChannelHistory();
        for (int i = 0; i <= 6; i++) h.Append(U0.AddSeconds(10 * i), 50 + i, .5);        // 0–60 s
        h.MarkGap(U0.AddSeconds(70));                                                       // 断线
        for (int i = 0; i <= 3; i++) h.Append(U0.AddSeconds(200 + 10 * i), 80 + i, .9);   // 重连后 200–230 s
        Assert.Equal(DateTime.SpecifyKind(U0.AddSeconds(230), DateTimeKind.Utc), h.LastDataAt);
        int gapsBefore = Enumerable.Range(0, h.Count).Count(i => h[i].Gap);
        Assert.True(gapsBefore >= 1);
        // 设备的 CSV：每秒一行（60–200 s），10 s 一格
        var rows = Enumerable.Range(61, 139).Select(s => new HistorySample(U0.AddSeconds(s), 60 + s / 10.0, .6, null, null, 1.5, null, null)).ToList();
        int n = h.Merge(rows, U0.AddSeconds(60), U0.AddSeconds(200));
        Assert.Equal(13, n);                                 // 70、80 … 190 s 这 13 格（61–69 s 落在 60 s 那一格，本机已有帧，不换）
        Assert.Equal(0, Enumerable.Range(0, h.Count).Count(i => h[i].Gap));   // 补齐了：没有断开
        // 已有的读数没被换掉
        Assert.Equal(50, h[0].TempC);
        Assert.Equal(83, h[h.Count - 1].TempC);
        // 按时间排序
        for (int i = 1; i < h.Count; i++) Assert.True(h[i].T > h[i - 1].T);
    }

    [Fact]
    public void ChannelHistory_Merge_LeavesAGap_WhereTheDeviceHasNoDataEither()
    {
        var h = new ChannelHistory();
        h.Append(U0, 50, .5);
        h.MarkGap(U0.AddSeconds(20));
        h.Append(U0.AddSeconds(300), 60, .5);
        // 设备也只有 0–60 s 的数据
        var rows = Enumerable.Range(1, 60).Select(s => new HistorySample(U0.AddSeconds(s), 51, .5, null, null, null, null, null)).ToList();
        Assert.Equal(6, h.Merge(rows, U0, U0.AddSeconds(300)));
        Assert.Contains(Enumerable.Range(0, h.Count), i => h[i].Gap);    // 60–300 s 之间照样断开
    }

    [Fact]
    public void DeviceHistory_Merge_AndLastDataAt()
    {
        var d = new DeviceHistory();
        Assert.Null(d.LastDataAt);
        d.Channel(1).Append(U0, 50, .5);
        d.Channel(2).Append(U0.AddSeconds(20), 50, .5);
        Assert.Equal(U0.AddSeconds(20), d.LastDataAt);
        int v = d.Version;
        var added = d.Merge(new Dictionary<int, List<HistorySample>>
        {
            [1] = new() { new HistorySample(U0.AddSeconds(15), 51, .5, null, null, null, null, null) },
            [3] = new() { new HistorySample(U0.AddSeconds(15), 52, .5, null, null, null, null, null) },
        }, U0, U0.AddSeconds(60));
        Assert.Equal(2, added);
        Assert.True(d.Version > v);
        Assert.True(d.Has(3));
    }

    [Fact]
    public void TelemetryCsv_ReadSamples_OpenInterval_PsiFallback_RowCount()
    {
        string T(int s) => new DateTimeOffset(U0.AddSeconds(s)).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        var csv = "﻿时间,RV1_PV(℃),RV1_压力(MPa),RV1_吸气量(mmol),RV2_PV(℃),RV2_压力(psi)\n"
            + T(0) + ",50,0.5,1,60,145.037738\n"     // = from：本机已有
            + T(1) + ",51,0.51,1.1,61,72.518869\n"
            + T(2) + ",52,,1.2,,\n"                   // RV2 这一行全空：不出采样
            + T(3) + ",53,0.53,1.3,63,0\n"
            + T(4) + ",54,0.54,1.4,64,0\n";           // = to：不要
        var (s, rows) = TelemetryCsvReader.ReadSamples(new StringReader(csv), U0, U0.AddSeconds(4));
        Assert.Equal(3, rows);
        Assert.Equal(new[] { 51.0, 52, 53 }, s[1].Select(x => x.TempC!.Value).ToArray());
        Assert.Null(s[1][1].PressureMPa);
        Assert.Equal(1.1, s[1][0].Mmol);
        Assert.Equal(DateTimeKind.Utc, s[1][0].T.Kind);
        Assert.Equal(U0.AddSeconds(1), s[1][0].T);
        Assert.Equal(2, s[2].Count);
        Assert.Equal(.5, s[2][0].PressureMPa!.Value, 6);          // psi → MPa
        Assert.Empty(TelemetryCsvReader.ReadSamples(new StringReader("a,b\n1,2\n"), U0, U0.AddSeconds(4)).Samples);
    }

    // ———————————————————— 保压类判据 = 设备设置的泄漏率阈值 ————————————————————

    [Fact]
    public void HoldVerdict_UsesTheDeviceLeakRate()
    {
        static List<RecPoint> Line(double dropPsiH) =>
            Enumerable.Range(0, 85).Select(i => i * .5).Select(m => new RecPoint(m, 25, .35 - dropPsiH / RecCompute.PsiPerMPa * m / 60, 0)).ToList();
        var s = new RecSession("LK-1", 1, "泄漏测试", "", "", "PC", new[] { 1, 2 }, new DateTime(2026, 11, 14, 9, 0, 0), new DateTime(2026, 11, 14, 10, 0, 0),
            RecState.Ended, 0, 0, null, null, RecHold.Leak);
        var set = new RecSeriesSet(new[] { new RecChannelSeries(1, Line(1.0)), new RecChannelSeries(2, Line(.3)) }, 42, false);
        var dflt = RecCompute.Compare(s, set);
        Assert.Equal((2.0, true, true), (dflt.HoldLimit, dflt.Rows[0].Ok, dflt.Rows[1].Ok));
        var strict = RecCompute.Compare(s, set, .5);
        Assert.Equal((.5, false, true), (strict.HoldLimit, strict.Rows[0].Ok, strict.Rows[1].Ok));
        Assert.Equal(2.0, RecCompute.Compare(s, set, double.NaN).HoldLimit);   // 不知道 → HMI 出厂值
        Assert.Contains("判据,≤ 0.5 psi/h", RecExport.ResultsCsv(s, "1号机", strict));
    }

    // ———————————————————— 性能测试计划：釜转速 ≤ 450 ————————————————————

    [Fact]
    public void PerfPlans_StirPoints_AreVesselRpm_AtMost450()
    {
        foreach (var plan in new[] { PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan), PlanLoader.LoadEmbedded(PlanLoader.SmokePlan) })
            foreach (var it in plan.AllItems())
            {
                if (it.Repeat?.Variable == "rpm" && it.Repeat.Values is { } vals)
                    Assert.All(vals, v => Assert.InRange(v, 0, 450));
                foreach (var st in it.Steps)
                {
                    if (st.Command is { Op: "stir", Args: { } args } && args.TryGetValue("rpm", out var rpm) && rpm.ValueKind == JsonValueKind.Number)
                        Assert.InRange(rpm.GetDouble(), 0, 450);
                    foreach (var f in st.Fields ?? new List<ManualField>())
                        if (f.Unit == "rpm") Assert.InRange(f.Max ?? 0, 0, 600);
                }
            }
        var s1 = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan).FindItem("S1")!;
        Assert.Equal(new[] { 100.0, 200, 300, 350, 400, 450, 450, 400, 350, 300, 200, 100 }, s1.Repeat!.Values!.ToArray());
    }
}
