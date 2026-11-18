using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Records;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F7 记录页的核心逻辑：8 路对比的算法（最大速率、到 90 %、统计、保压类压降、平均线）、文字、遥测 CSV 的读取、演示会话（样稿 SESS / rcGas / rcData 历史分支）、导出。
/// </summary>
public class F7RecordsTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 9, 15, 0, DateTimeKind.Local);

    private static RecSession Sess(RecState st = RecState.Ended, RecHold hold = RecHold.None, string? warn = null, params int[] chs) =>
        new("PT-1", 1, "测试", "平行合成程序 A", "张工", "PC", chs.Length > 0 ? chs : new[] { 1, 2, 3 }, T0, st == RecState.Active ? null : T0.AddMinutes(60), st, 0, 0, null, warn, hold);

    private static List<RecPoint> Line(double step, double end, Func<double, double> g, Func<double, double>? p = null)
    {
        var l = new List<RecPoint>();
        for (double m = 0; m <= end + 1e-9; m += step) l.Add(new RecPoint(m, 25, p?.Invoke(m) ?? .1, g(m)));
        return l;
    }

    // ———————————————————— 算法 ————————————————————

    [Fact]
    public void MaxRate_UsesTwoMinuteWindow_AtLeastTwoSamples()
    {
        // 线性 0.5 mmol/min：任何窗口都是 0.5
        Assert.Equal(.5, RecCompute.MaxRate(Line(.5, 30, m => m * .5)), 6);
        // 10 s 一点、1 min 内的台阶：2 min 窗口把它摊开 → 1 / 2
        var step = Line(1.0 / 6, 30, m => m >= 10 ? 1 : 0);
        Assert.Equal(.5, RecCompute.MaxRate(step), 2);
        // 1.36 min 一点：k = 2（窗口 2.72 min）
        var coarse = Line(1.36, 30, m => m >= 10 ? 2.72 : 0);
        Assert.Equal(1, RecCompute.MaxRate(coarse), 6);
        Assert.Equal(0, RecCompute.MaxRate(new List<RecPoint> { new(0, 0, 0, 1) }));
    }

    [Fact]
    public void T90_FirstSampleAtNinetyPercent()
    {
        var pts = Line(1, 60, m => m < 20 ? 0 : Math.Min(10, (m - 20) * .5));
        Assert.Equal(38, RecCompute.T90(pts, 10));
        Assert.Null(RecCompute.T90(pts, 0));
        Assert.Null(RecCompute.T90(pts, 100));
    }

    [Fact]
    public void Stats_SampleSdAndRsd()
    {
        var rows = new[] { 10.0, 12, 14 }.Select((g, i) => new RecRow(i + 1, Array.Empty<RecPoint>(), true, g, 0, null, false, "完成", RecTone.Normal)).ToList();
        rows.Add(new RecRow(4, Array.Empty<RecPoint>(), false, 0, 0, null, false, "未运行", RecTone.Normal));
        var st = RecCompute.Stats(rows);
        Assert.Equal(3, st.N);
        Assert.Equal(12, st.Mean, 9);
        Assert.Equal(2, st.Sd, 9);
        Assert.Equal(100.0 / 6, st.Rsd, 9);
        Assert.Equal("+17%", RecTexts.Pct(RecCompute.Rel(14, 12)));
        Assert.Equal("−17%", RecTexts.Pct(RecCompute.Rel(10, 12)));
    }

    [Fact]
    public void Compare_Historical_UsedOnlyWithUptake_InterruptedAndWarnTones()
    {
        var set = new RecSeriesSet(new[]
        {
            new RecChannelSeries(1, Line(1, 60, m => m / 6)),
            new RecChannelSeries(2, Line(1, 60, _ => 0)),
            new RecChannelSeries(3, Line(1, 60, m => m / 4)),
        }, 60, false);
        var c = RecCompute.Compare(Sess(warn: "RV3 升温超时（提醒）"), set);
        Assert.Equal(new[] { 2 }, c.Skipped);
        Assert.Equal(("完成", RecTone.Normal), (c.Rows[0].Result, c.Rows[0].Tone));
        Assert.Equal(("未运行", false), (c.Rows[1].Result, c.Rows[1].Used));
        Assert.Equal(("有提醒", RecTone.Warn), (c.Rows[2].Result, c.Rows[2].Tone));
        Assert.Equal(2, c.Stats.N);
        Assert.Equal(12.5, c.Stats.Mean, 6);
        Assert.Equal(54, c.Rows[0].T90);
        var ci = RecCompute.Compare(Sess(RecState.Interrupted), set);
        Assert.All(ci.Rows.Where(r => r.Used), r => Assert.Equal(("中断", RecTone.Warn), (r.Result, r.Tone)));
        // 没有数据的通道
        var c2 = RecCompute.Compare(Sess(chs: new[] { 1, 9 }), set);
        Assert.Equal(("无数据", false), (c2.Rows[1].Result, c2.Rows[1].Used));
    }

    [Fact]
    public void Compare_Live_ReadyNotCounted_ReactingHasNoT90_AlarmPaused()
    {
        var up = Line(.5, 80, m => m < 20 ? 0 : Math.Min(18, m - 20));
        var set = new RecSeriesSet(new[]
        {
            new RecChannelSeries(1, up, new RecLiveState(true, true, false, false, "反应中", 18.4)),
            new RecChannelSeries(4, up, new RecLiveState(true, false, false, true, "压力超限", 15.9)),
            new RecChannelSeries(5, Line(.5, 80, _ => 0), new RecLiveState(true, false, false, false, "已就绪", 0)),
            new RecChannelSeries(8, up, new RecLiveState(true, true, true, false, "降温中", 18)),
        }, 85, true);
        var c = RecCompute.Compare(Sess(RecState.Active, chs: new[] { 1, 4, 5, 8 }), set);
        Assert.True(c.Live);
        RecRow R(int n) => c.Rows.Single(r => r.N == n);
        Assert.Equal((18.4, true, (double?)null), (R(1).G, R(1).Live, R(1).T90));
        Assert.Equal(("已暂停", RecTone.Err, true, (double?)null), (R(4).Result, R(4).Tone, R(4).Live, R(4).T90));
        Assert.Equal(("已就绪", false), (R(5).Result, R(5).Used));
        Assert.Equal((false, 36.5), (R(8).Live, R(8).T90!.Value));     // 降温段：终值已定，算「到 90 %」
        Assert.Equal(new[] { 5 }, c.Skipped);
        Assert.Equal(3, c.Stats.N);
        Assert.Equal("（RV5 未运行，不计入）", RecTexts.SkipLine(c.Skipped));
        Assert.Equal("平均 · SD 1.3 · RSD 7.7 % · 3 路", RecTexts.StatsLine(c.Stats));
    }

    [Fact]
    public void HoldDrop_LeastSquaresSlope_InPsiPerHour_PassUnderTwo()
    {
        // 0.35 MPa 起、每小时降 1.5 psi（6 min 之后）
        var p = Line(.5, 42, _ => 0, m => m < 6 ? .35 * m / 6 : .35 - 1.5 / RecCompute.PsiPerMPa * (m - 6) / 60);
        Assert.Equal(1.5, RecCompute.HoldDrop(p), 6);
        var fast = Line(.5, 42, _ => 0, m => .35 - 3 / RecCompute.PsiPerMPa * m / 60);
        var set = new RecSeriesSet(new[] { new RecChannelSeries(1, p), new RecChannelSeries(2, fast), new RecChannelSeries(3, new List<RecPoint>()) }, 42, false);
        var c = RecCompute.Compare(Sess(hold: RecHold.Leak), set);
        Assert.True(c.IsHold);
        Assert.Equal(("通过", true), (c.Rows[0].Result, c.Rows[0].Ok));
        Assert.Equal(("不通过", false, RecTone.Err), (c.Rows[1].Result, c.Rows[1].Ok, c.Rows[1].Tone));
        Assert.Equal(("无数据", false), (c.Rows[2].Result, c.Rows[2].Used));
        double p1 = .35 - 1.5 / RecCompute.PsiPerMPa * 4 / 60, p2 = .35 - 3 / RecCompute.PsiPerMPa * 10 / 60;   // 第 10 min 的读数
        Assert.Equal((p1 + p2) / 2, c.TestMPa!.Value, 9);
        Assert.Equal("惰性气体 · 约 50 psi", RecTexts.HoldGasLine(.35));
        Assert.Equal("惰性气体 · 约 145 psi", RecTexts.HoldGasLine(1.0));
        Assert.True(double.IsNaN(RecCompute.HoldDrop(Line(1, 5, _ => 0))));
    }

    [Fact]
    public void MeanLine_InterpolatesOnDensestGrid()
    {
        var a = new RecRow(1, Line(1, 10, m => m), true, 10, 0, null, false, "", RecTone.Normal);
        var b = new RecRow(2, Line(2, 10, m => 2 * m), true, 20, 0, null, false, "", RecTone.Normal);
        var mean = RecCompute.MeanLine(new[] { a, b }, p => p.G);
        Assert.Equal(11, mean.Count);
        Assert.Equal(1.5 * 3, mean[3].V, 9);
        Assert.Empty(RecCompute.MeanLine(new[] { a }, p => p.G));
    }

    [Fact]
    public void ContainsRv_DoesNotMatchPrefix()
    {
        Assert.True(RecCompute.ContainsRv("RV3 升温超时（提醒）", 3));
        Assert.False(RecCompute.ContainsRv("RV12 超压", 1));
        Assert.True(RecCompute.ContainsRv("RV12 超压、RV1 慢", 1));
    }

    // ———————————————————— 文字 ————————————————————

    [Fact]
    public void Texts_DurationDayClockTags()
    {
        Assert.Equal("1 h 25 min", RecTexts.Dur(TimeSpan.FromMinutes(85.1)));
        Assert.Equal("4 h 05 min", RecTexts.Dur(TimeSpan.FromMinutes(245)));
        Assert.Equal("42 min", RecTexts.Dur(TimeSpan.FromMinutes(42)));
        Assert.Equal("0 min", RecTexts.Dur(TimeSpan.FromSeconds(-5)));
        var today = new DateTime(2026, 11, 15, 14, 30, 7);
        Assert.Equal("今天", RecTexts.DayGroup(new DateTime(2026, 11, 15, 9, 12, 0), today));
        Assert.Equal("昨天", RecTexts.DayGroup(new DateTime(2026, 11, 14, 23, 0, 0), today));
        Assert.Equal("更早", RecTexts.DayGroup(new DateTime(2026, 11, 13, 14, 2, 0), today));
        Assert.Equal("13:20", RecTexts.Clock(new DateTime(2026, 11, 15, 13, 5, 0), 15));
        Assert.Equal("13:06", RecTexts.Clock(new DateTime(2026, 11, 15, 13, 5, 0), 0.5));
        Assert.Equal(("进行中", RecTagTone.Run), RecTexts.Tag(Sess(RecState.Active)));
        Assert.Equal(("中断", RecTagTone.Warn), RecTexts.Tag(Sess(RecState.Interrupted)));
        Assert.Equal(("完成 · 有提醒", RecTagTone.Warn), RecTexts.Tag(Sess(warn: "RV3")));
        Assert.Equal(("已结束", RecTagTone.Plain), RecTexts.Tag(Sess()));
        Assert.Equal("泄漏测试", RecTexts.ProgramText(Sess(hold: RecHold.Leak) with { Program = "—" }));
    }

    // ———————————————————— 遥测 CSV ————————————————————

    private const string Header = "时间,标记,会话,RV1_PV(℃),RV1_压力(psi),RV1_压力(MPa),RV2_PV(℃),RV2_压力(psi),RV2_压力(MPa),模拟,RV1_吸气量(mmol),RV2_吸气量(mmol)";

    private static string Row(DateTime t, string mark, double? p1, double? g1, double? g2, double? psi2 = 50) =>
        string.Join(",", t.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture), mark, "PT-1",
            "80.5", "", p1?.ToString(CultureInfo.InvariantCulture) ?? "", "81", psi2?.ToString(CultureInfo.InvariantCulture) ?? "", "", "0",
            g1?.ToString(CultureInfo.InvariantCulture) ?? "", g2?.ToString(CultureInfo.InvariantCulture) ?? "");

    [Fact]
    public void Csv_ReadsByHeader_FiltersWindow_PsiFallback_QuotedFields()
    {
        var csv = "﻿" + Header + "\n"
            + Row(T0.AddMinutes(-1), "", .3, 1, 1) + "\n"
            + Row(T0, "\"加料, 第 1 批\"", .35, 0, 0) + "\n"
            + Row(T0.AddMinutes(1), "\"带\"\"引号\"\"\n换行\"", .36, 2.5, null) + "\n"
            + Row(T0.AddMinutes(2), "", null, 3.0, 1.0, null) + "\r\n"
            + Row(T0.AddMinutes(70), "", .3, 9, 9) + "\n";
        var d = TelemetryCsvReader.ReadText(csv, T0, T0.AddMinutes(60));
        Assert.Equal(new[] { 1, 2 }, d.Keys.ToArray());
        Assert.Equal(3, d[1].Count);
        Assert.Equal(new[] { 0.0, 1, 2 }, d[1].Select(p => p.M).ToArray());
        Assert.Equal(new[] { 0.0, 2.5, 3 }, d[1].Select(p => p.G).ToArray());
        Assert.Equal(.36, d[1][1].P, 9);
        Assert.True(double.IsNaN(d[1][2].P));
        Assert.Equal(50 / RecCompute.PsiPerMPa, d[2][0].P, 9);   // 没有 MPa 列的值时用 psi 换算
        Assert.True(double.IsNaN(d[2][1].G));
        Assert.Equal(80.5, d[1][0].T, 9);
        // 抽稀：6 s 一点
        var dense = Header + "\n" + string.Join("\n", Enumerable.Range(0, 100).Select(i => Row(T0.AddSeconds(i * 1.2), "", .35, i, i)));
        Assert.Equal(20, TelemetryCsvReader.ReadText(dense, T0, null, 6)[1].Count);
    }

    [Fact]
    public void Csv_OldFileWithoutUptakeColumn_GIsNaN_NoTimeColumn_Empty()
    {
        var csv = "时间,RV1_PV(℃),RV1_压力(MPa)\n" + T0.ToString("o", CultureInfo.InvariantCulture) + ",25,0.35\n";
        var d = TelemetryCsvReader.ReadText(csv, T0, null);
        Assert.True(double.IsNaN(d[1][0].G));
        Assert.Empty(TelemetryCsvReader.ReadText("a,b\n1,2\n", T0, null));
        Assert.Empty(TelemetryCsvReader.ReadText("", T0, null));
    }

    // ———————————————————— 演示会话 ————————————————————

    [Fact]
    public void Demo_SessionsMatchTheMock()
    {
        var all = DemoRecords.Sessions(_ => true);
        Assert.Equal(new[] { "PT-20261115-1305", "PT-20261115-1250", "LK-20261115-0912", "PT-20261114-0915", "PT-20261113-1402", "PT-20261112-0930", "PT-20261110-1600" },
            all.Select(s => s.Id).ToArray());
        var live = all[0];
        Assert.Equal((RecState.Active, (DateTime?)null, 7, 3, 2), (live.State, live.End, live.Channels.Count, live.Marks, live.Alarms));
        Assert.DoesNotContain(7, live.Channels);
        Assert.Equal((RecHold.Leak, 16), (all[2].Hold, all[2].Channels.Count));
        Assert.Equal(RecHold.Tight, all[6].Hold);
        Assert.Equal(T0.AddMinutes(245), all[3].End);
        Assert.Equal("断电重启 · 会话中断，数据保留到 16:20", all[4].Note);
        Assert.Equal(new[] { "PT-20261115-1305", "PT-20261114-0915", "PT-20261112-0930", "PT-20261110-1600" },
            DemoRecords.Sessions(k => k == 1).Select(s => s.Id).ToArray());
        Assert.Equal(245, DemoRecords.Minutes("4 h 05 min"));
        Assert.Equal(42, DemoRecords.Minutes("42 min"));
    }

    [Fact]
    public void Demo_History_SaturatingCurves_FinalValuesAsRcGas()
    {
        var s = DemoRecords.Sessions(_ => true).Single(x => x.Id == "PT-20261114-0915");
        var set = DemoRecords.Series(s)!;
        Assert.False(set.Live);
        Assert.Equal(245, set.Span);
        var c = RecCompute.Compare(s, set);
        // 样稿 rec-ch 参照图的吸气终值、最高 / 最低、到 90 % 用时
        Assert.Equal(new[] { 21.2, 15.7, 18.4, 14.4, 21.5, 16.1, 15.6, 16.7 }, c.Rows.Select(r => Math.Round(r.G, 1)).ToArray());
        Assert.Equal("17.4", RecTexts.F1(c.Stats.Mean));
        Assert.Equal("2.7", RecTexts.F1(c.Stats.Sd));
        Assert.Equal("15.2", RecTexts.F1(c.Stats.Rsd));
        Assert.Equal(new[] { 159, 137, 191, 188, 157, 154, 186, 178 }, c.Rows.Select(r => (int)Math.Round(r.T90!.Value)).ToArray());
        // 饱和：单调不减、开头为 0
        foreach (var ch in set.Channels)
        {
            Assert.Equal(0, ch.Points[0].G);
            for (int i = 1; i < ch.Points.Count; i++) Assert.True(ch.Points[i].G >= ch.Points[i - 1].G - 1e-12);
        }
        // 确定性
        Assert.Equal(set.Channels[3].Points.Select(p => p.T), DemoRecords.Series(s)!.Channels[3].Points.Select(p => p.T));
        Assert.Null(DemoRecords.Series(DemoRecords.Sessions(_ => true)[0]));   // 进行中的会话用历史缓存
    }

    [Fact]
    public void Demo_Leak_DropsCloseToTheMock_AllPass()
    {
        var s = DemoRecords.Sessions(_ => true).Single(x => x.Id == "LK-20261115-0912");
        var c = RecCompute.Compare(s, DemoRecords.Series(s)!);
        Assert.True(c.IsHold);
        Assert.All(c.Rows, r => Assert.True(r.Ok));
        // 样稿 rec-hold 参照图：0.54 0.40 0.69 0.29 0.76 1.00 0.77 0.74 0.69 0.40（按回归算出来的压降，误差 < 0.03）
        var want = new[] { .54, .40, .69, .29, .76, 1.00, .77, .74, .69, .40 };
        for (int i = 0; i < want.Length; i++) Assert.InRange(c.Rows[i].Drop, want[i] - .03, want[i] + .03);
        Assert.Equal("0.35", RecTexts.F2(c.TestMPa!.Value));
        Assert.Equal(4, DemoRecords.Files(s).Count);
        Assert.Empty(DemoRecords.Marks(s));
    }

    [Fact]
    public void Export_ResultsAndSeriesCsv()
    {
        var s = DemoRecords.Sessions(_ => true).Single(x => x.Id == "PT-20261114-0915");
        var set = DemoRecords.Series(s)!;
        var c = RecCompute.Compare(s, set);
        var csv = RecExport.ResultsCsv(s, "1号机", c);
        Assert.Contains("RV1,21.2,", csv);
        Assert.Contains("RSD %,15.2", csv);
        var data = RecExport.SeriesCsv(s, set);
        var back = TelemetryCsvReader.ReadText(data, s.Start, s.End);
        Assert.Equal(8, back.Count);
        Assert.Equal(set.Channels[0].Points.Count, back[1].Count);
        Assert.Equal(21.2, RecCompute.LastG(back[1]), 2);
        var hold = DemoRecords.Sessions(_ => true).Single(x => x.Hold == RecHold.Leak);
        Assert.Contains("判据,≤ 2.0 psi/h", RecExport.ResultsCsv(hold, "3号机", RecCompute.Compare(hold, DemoRecords.Series(hold)!)));
    }
}
