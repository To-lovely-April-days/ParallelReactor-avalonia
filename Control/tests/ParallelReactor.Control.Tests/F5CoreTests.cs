using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Events;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F5 的纯计算：报警页（Core.Alarms.AlarmLog：报警名 / 读数拆分、报警簿 + 事件簿合表与去重、筛选、指标、时间文字、CSV）、
/// 曲线页（Core.History.CurveStats：时间窗、纵轴 / 横轴刻度、区间统计、光标读数、RSD、CSV），以及演示数据里已解除的报警（样稿 ALOG 的 H1–H4）。
/// </summary>
public class F5CoreTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 14, 30, 7, DateTimeKind.Local);
    private static DateTime At(int h, int m, int s = 0, int day = 15) => new(2026, 11, day, h, m, s, DateTimeKind.Local);

    // ———————————————————— 报警名 / 读数 ————————————————————

    [Theory]
    [InlineData("压力超上限 · 已关闭进气阀 · 程序暂停", "压力超上限", "已关闭进气阀 · 程序暂停")]
    [InlineData("超温 · 切断加热 · PV 205.3℃ ≥ 超温限值 200℃", "超温 · 切断加热", "PV 205.3℃ ≥ 超温限值 200℃")]
    [InlineData("超压 · 联锁动作 · 压力 3.55 MPa ≥ 超压报警值 3.50 MPa", "超压 · 联锁动作", "压力 3.55 MPa ≥ 超压报警值 3.50 MPa")]
    [InlineData("超温 · 切断加热", "超温 · 切断加热", "")]
    [InlineData("升温偏慢", "升温偏慢", "")]
    [InlineData("超温切断 · 其余", "超温切断", "其余")]   // 「超温 · 切断加热」不是它的开头
    public void SplitTitle_KnownTitlesWin_ElseFirstDot(string text, string title, string more) =>
        Assert.Equal((title, more), AlarmLog.SplitTitle(text));

    [Fact]
    public void SplitTitle_CallerTitles()
    {
        Assert.Equal(("甲 · 乙", "丙"), AlarmLog.SplitTitle("甲 · 乙 · 丙", new[] { "甲 · 乙" }));
        Assert.Equal(("甲", "乙 · 丙"), AlarmLog.SplitTitle("甲 · 乙 · 丙"));
    }

    [Fact]
    public void SplitValue_HmiReadingSegment()
    {
        Assert.Equal(("205.3 °C", "上限 200 °C", "已切断 RV6 加热"), AlarmLog.SplitValue("PV 205.3℃ ≥ 超温限值 200℃ · 已切断 RV6 加热"));
        Assert.Equal(("3.55 MPa", "上限 3.50 MPa", "原状态 反应中"), AlarmLog.SplitValue("原状态 反应中 · 压力 3.55 MPa ≥ 超压报警值 3.50 MPa"));
        Assert.Equal(("62 min", "上限 60 min", "仅提醒"), AlarmLog.SplitValue("已用 62 min ≥ 超时 60 min · 仅提醒"));
        Assert.Equal(((string?)null, (string?)null, "第 1 步已用 18 min"), AlarmLog.SplitValue("第 1 步已用 18 min"));
        Assert.Equal(((string?)null, (string?)null, ""), AlarmLog.SplitValue(""));
    }

    // ———————————————————— 合表 ————————————————————

    private static AlarmEntry A(string id, AlarmLevel lv, int? ch, string text, DateTime at, bool acked = false, bool active = true, DateTime? cleared = null, string? by = null, string code = "ALARM") =>
        new(id, id, lv, ch, code, text, at, acked, by, active, cleared, Condition: true);

    private static EventEntry E(string key, DateTime at, EventLevel lv, int? n, string text) => new(key, at, lv, n, text);

    private static AlarmLogSource Dev1(Func<AlarmEntry, AlarmReading?>? reading = null) => new(1, "1号机", "HT-PR8",
        new[]
        {
            A("a1", AlarmLevel.Err, 4, "压力超上限 · 已关闭进气阀 · 程序暂停", At(14, 3, 18), code: AlarmCodes.PressureHigh),
            A("a2", AlarmLevel.Warn, 2, "升温偏慢 · 第 1 步已用 18 min", At(14, 1, 30)),
            A("h2", AlarmLevel.Err, 6, "超温 · 切断加热 · PV 205.3℃ ≥ 超温限值 200℃ · 已切断 RV6 加热", At(11, 22, 10), acked: true, active: false, cleared: At(11, 24, 52), by: "PC-测试工位1"),
            A("h4", AlarmLevel.Warn, 2, "升温超时 · 已用 62 min ≥ 超时 60 min · 仅提醒", At(16, 47, 20, 14), acked: true, active: false, cleared: At(16, 51, 2, 14)),
        },
        new[]
        {
            E("e1", At(14, 5), EventLevel.Op, null, "控制权 · PC-测试工位1 获得远程控制"),
            E("e2", At(14, 3, 18), EventLevel.Err, 4, "RV4 压力超上限"),          // 与 a1 重复
            E("e3", At(13, 58), EventLevel.Info, 2, "RV2 程序「平行合成程序 A」开始"),
            E("e4", At(11, 22, 12), EventLevel.Err, 6, "RV6 超温 · 切断加热"),     // 与 h2 相差 2 s：重复
            E("e5", At(9, 0), EventLevel.Err, 3, "RV3 超压"),                      // 报警簿里没有：已记录
            E("e6", At(9, 1), EventLevel.Info, null, "整机自检通过"),
        }, reading);

    [Fact]
    public void Build_MergesAlarmsAndEvents_DedupesAndSortsNewestFirst()
    {
        var rows = AlarmLog.Build(new[] { Dev1() });
        Assert.Equal(new[] { "E1:e1", "A1:a1", "A1:a2", "E1:e3", "A1:h2", "E1:e6", "E1:e5", "A1:h4" }, rows.Select(r => r.Key));
        var ev = rows[0];
        Assert.Equal(("控制权", "PC-测试工位1 获得远程控制", AlarmLogLevel.Info, AlarmLogState.Info), (ev.Src, ev.Title, ev.Level, ev.State));
        var a1 = rows[1];
        Assert.Equal(("RV4", "压力超上限", "已关闭进气阀 · 程序暂停", AlarmLogState.Active, true), (a1.Src, a1.Title, a1.Act, a1.State, a1.Unacked));
        Assert.Null(a1.Val);   // 没有读数段、也没给当前读数
        var h2 = rows.Single(r => r.Key == "A1:h2");
        Assert.Equal(("超温 · 切断加热", "205.3 °C", "上限 200 °C", "已切断 RV6 加热", AlarmLogState.Cleared, false), (h2.Title, h2.Val, h2.Lim, h2.Act, h2.State, h2.Unacked));
        Assert.Equal(("已解除 11:24:52", "已确认 · PC-测试工位1"), (AlarmLog.StateText(h2), AlarmLog.AckText(h2)));
        var e3 = rows.Single(r => r.Key == "E1:e3");
        Assert.Equal(("RV2", "程序「平行合成程序 A」开始"), (e3.Src, e3.Title));
        var e5 = rows.Single(r => r.Key == "E1:e5");
        Assert.Equal((AlarmLogLevel.Hi, AlarmLogState.Logged, false, "已记录", "设备事件记录"), (e5.Level, e5.State, e5.Unacked, AlarmLog.StateText(e5), AlarmLog.AckText(e5)));
        Assert.Equal("整机", rows.Single(r => r.Key == "E1:e6").Src);
    }

    [Fact]
    public void Build_ReadingFallback_OnlyWhenTextHasNone()
    {
        var rows = AlarmLog.Build(new[] { Dev1(a => a.Ch == 4 ? new AlarmReading("3.55 MPa", "上限 3.50 MPa") : null) });
        Assert.Equal(("3.55 MPa", "上限 3.50 MPa"), (rows[1].Val, rows[1].Lim));
        Assert.Equal("205.3 °C", rows.Single(r => r.Key == "A1:h2").Val);   // 文字里的读数优先
    }

    [Fact]
    public void LiveEvents_UseHmiTitleOriginMessage()
    {
        var e = new EventEntry("k", At(14, 5), EventLevel.Op, 3, "RV3 …", Title: "加料", Message: "5 mL", Origin: "系统");
        var r = AlarmLog.FromEvent(new AlarmLogSource(1, "1号机", "HT-PR8", Array.Empty<AlarmEntry>(), Array.Empty<EventEntry>()), e);
        Assert.Equal(("RV3", "加料", "5 mL"), (r.Src, r.Title, r.Act));
    }

    [Fact]
    public void SrcOf_DeviceLevel()
    {
        Assert.Equal("通讯", AlarmLog.SrcOf(A("x", AlarmLevel.Err, null, "压力采集模块离线", Now, code: AlarmCodes.Comm)));
        Assert.Equal("搅拌", AlarmLog.SrcOf(A("x", AlarmLevel.Err, null, "搅拌电机故障 · 报警码", Now)));
        Assert.Equal("急停", AlarmLog.SrcOf(A("x", AlarmLevel.Err, null, "急停已触发", Now, code: AlarmCodes.Estop)));
        Assert.Equal("整机", AlarmLog.SrcOf(A("x", AlarmLevel.Err, null, "其他", Now)));
    }

    // ———————————————————— 筛选 / 指标 ————————————————————

    [Fact]
    public void Filter_RangeLevelStateQuery()
    {
        var rows = AlarmLog.Build(new[] { Dev1() });
        IReadOnlyList<string> K(AlarmLogFilter f, int k = 1) => AlarmLog.Filter(rows, k, f, Now).Select(r => r.Key).ToList();
        Assert.Equal(7, K(new AlarmLogFilter()).Count);                                     // 今天（昨天的 h4 不在）
        Assert.Equal(8, K(new AlarmLogFilter(AlarmLogRange.Week)).Count);
        Assert.Equal(new[] { "A1:a1", "A1:h2", "E1:e5" }, K(new AlarmLogFilter(Level: AlarmLogLevelFilter.Hi)));
        Assert.Equal(new[] { "A1:a2" }, K(new AlarmLogFilter(Level: AlarmLogLevelFilter.Lo)));
        Assert.Equal(new[] { "E1:e1", "E1:e3", "E1:e6" }, K(new AlarmLogFilter(Level: AlarmLogLevelFilter.Ev)));
        Assert.Equal(new[] { "A1:a1", "A1:a2" }, K(new AlarmLogFilter(State: AlarmLogStateFilter.Unacked)));
        Assert.Equal(new[] { "A1:a1", "A1:a2" }, K(new AlarmLogFilter(State: AlarmLogStateFilter.Active)));
        Assert.Equal(new[] { "A1:h2", "A1:h4" }, K(new AlarmLogFilter(AlarmLogRange.Week, State: AlarmLogStateFilter.Cleared)));
        Assert.Equal(new[] { "A1:h2" }, K(new AlarmLogFilter(Query: "超温")));
        Assert.Equal(new[] { "A1:a1" }, K(new AlarmLogFilter(Query: "rv4")));              // 不分大小写，搜来源
        Assert.Equal(7, K(new AlarmLogFilter(Query: "1号机")).Count);                      // 搜设备名
        Assert.Empty(K(new AlarmLogFilter(), k: 2));
    }

    [Fact]
    public void Kpis_CountActiveUnackedToday()
    {
        var rows = AlarmLog.Build(new[] { Dev1() });
        Assert.Equal(new AlarmLogKpis(1, 2, 1, 7), AlarmLog.Kpis(rows, 1, Now));
        Assert.Equal(new AlarmLogKpis(1, 2, 1, 7), AlarmLog.Kpis(rows, 0, Now));
        Assert.Equal(new AlarmLogKpis(0, 0, 0, 0), AlarmLog.Kpis(rows, 2, Now));
    }

    [Fact]
    public void TimeTexts()
    {
        Assert.Equal("14:03:18", AlarmLog.TimeText(At(14, 3, 18), Now));
        Assert.Equal("昨天 16:47:20", AlarmLog.TimeText(At(16, 47, 20, 14), Now));
        Assert.Equal("11-12 09:30:00", AlarmLog.TimeText(At(9, 30, 0, 12), Now));
        Assert.Equal("今天 14:03:18", AlarmLog.WhenText(At(14, 3, 18), Now));
        Assert.Equal("2026-11-12 09:30:00", AlarmLog.WhenText(At(9, 30, 0, 12), Now));
    }

    [Fact]
    public void Csv_HeaderEscapingAndAckColumn()
    {
        var rows = AlarmLog.Build(new[] { Dev1() });
        var csv = AlarmLog.Csv(rows.Take(2)).Split('\n');
        Assert.Equal("日期,时间,设备,型号,级别,来源,内容,说明,值,限值,状态,解除时刻,确认", csv[0]);
        Assert.Equal("2026-11-15,14:05:00,1号机,HT-PR8,事件,控制权,PC-测试工位1 获得远程控制,,,,事件,,", csv[1]);
        Assert.Equal("2026-11-15,14:03:18,1号机,HT-PR8,报警,RV4,压力超上限,已关闭进气阀 · 程序暂停,,,活动,,未确认", csv[2]);
        Assert.Equal("\"a,b\"", AlarmLog.Esc("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", AlarmLog.Esc("say \"hi\""));
    }

    // ———————————————————— 演示：已解除的报警 ————————————————————

    [Fact]
    public void DemoFusion_ClearedAlarms_H1ToH4()
    {
        var f = new DemoFleet(DemoParams.Parse("fusion=1&freeze=1"));
        var d1 = f.ClearedAlarms(1);
        Assert.Equal(new[] { "h2", "h4" }, d1.Select(a => a.Id));
        Assert.All(d1, a => Assert.True(!a.Active && a.Acked && a.ClearedAt != null));
        Assert.Equal((6, AlarmCodes.TempHigh), (d1[0].Ch!.Value, d1[0].Code));
        Assert.Equal(AlarmCodes.Comm, Assert.Single(f.ClearedAlarms(2)).Code);
        Assert.Equal("h3", Assert.Single(f.ClearedAlarms(3)).Id);
        var plain = new DemoFleet(DemoParams.Parse("freeze=1"));
        Assert.Empty(plain.ClearedAlarms(1));
    }

    // ———————————————————— 曲线 ————————————————————

    [Fact]
    public void Window_RangesAndRunClip()
    {
        var end = At(14, 30, 7);
        Assert.Equal((end.AddMinutes(-5), end, false), CurveStats.Window(CurveRange.M5, end, At(13, 0)));
        Assert.Equal((end.AddMinutes(-60), end, false), CurveStats.Window(CurveRange.H1, end, At(13, 0)));
        Assert.Equal((At(13, 0), end, false), CurveStats.Window(CurveRange.Run, end, At(13, 0, 4)));      // 取整到分钟
        var (a, _, clipped) = CurveStats.Window(CurveRange.Run, end, At(8, 0));
        Assert.True(clipped);
        Assert.Equal(end.AddMinutes(-240), a);
        Assert.Equal((end.AddMinutes(-60), end, false), CurveStats.Window(CurveRange.Run, end, null));
        Assert.Equal(end.AddMinutes(-1), CurveStats.Window(CurveRange.Run, end, end).A);                 // 至少 1 min
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(15, 5)]
    [InlineData(60, 10)]
    [InlineData(90, 15)]
    [InlineData(240, 30)]
    public void TickMinutes(double span, int want) => Assert.Equal(want, CurveStats.TickMinutes(span));

    [Fact]
    public void Axis_MatchesTheMock()
    {
        // 样稿 1号机全程：温度 24…160 → 0–200 每 50；压力最高 3.55（上限 3.50）→ 0–4 每 1；吸气 0…21.3 → 0–25 每 5
        Assert.Equal(new CurveAxis(0, 200, 50), CurveStats.Axis(HistoryField.TempC, new[] { 24.6, 160.0 }));
        Assert.Equal(new CurveAxis(0, 4, 1), CurveStats.Axis(HistoryField.PressureMPa, new[] { 0.01, 3.55 }, 3.5));
        Assert.Equal(new CurveAxis(0, 25, 5), CurveStats.Axis(HistoryField.Mmol, new[] { 0, 21.3 }));
        Assert.Equal(new CurveAxis(0, 75, 25), CurveStats.Axis(HistoryField.TempC, new[] { 25.0, 60 }));
        Assert.Equal(new CurveAxis(0, 1.5, .5), CurveStats.Axis(HistoryField.PressureMPa, new[] { 1.2 }));
        Assert.Equal(new CurveAxis(0, 1.5, .5), CurveStats.Axis(HistoryField.PressureMPa, Array.Empty<double>()));   // 同样稿：1 × 1.04 往上取整
        Assert.Equal(new CurveAxis(0, 60, 10), CurveStats.Axis(HistoryField.Mmol, new[] { 55.0 }));
        Assert.Equal(new CurveAxis(0, 250, 50), CurveStats.Axis(HistoryField.Mmol, new[] { 230.0 }));
    }

    private static ChannelHistory Hist()
    {
        var h = new ChannelHistory();
        // 13:00 起每 10 s：温度 = 20 + 分钟，压力 = 分钟 / 100，吸气 = 分钟 / 10；13:30–13:31 断开
        for (int i = 0; i <= 360; i++)
        {
            var t = At(13, 0).AddSeconds(i * 10);
            double m = i / 6.0;
            if (m > 30 && m < 31) continue;
            h.Append(t, 20 + m, m / 100, mmol: m / 10);
        }
        return h;
    }

    [Fact]
    public void Stat_At_CopyRange()
    {
        var h = Hist();
        var s = CurveStats.Stat(h, HistoryField.TempC, At(13, 10), At(13, 20))!.Value;
        Assert.Equal((30, 40, 35), (s.Min, s.Max, Math.Round(s.Mean, 6)));
        Assert.Equal(1.0, Math.Round(CurveStats.Stat(h, HistoryField.Mmol, At(13, 10), At(13, 20))!.Value.Rise, 6));
        Assert.Null(CurveStats.Stat(h, HistoryField.HeaterPct, At(13, 0), At(14, 0)));
        Assert.Null(CurveStats.Stat(null, HistoryField.TempC, At(13, 0), At(14, 0)));
        Assert.Equal(35, CurveStats.At(h, HistoryField.TempC, At(13, 15, 2), At(13, 0), At(14, 0)));
        Assert.Equal(80, CurveStats.At(h, HistoryField.TempC, At(15, 0), At(13, 0), At(14, 0)));            // 光标夹在窗口里
        Assert.Equal(50, CurveStats.At(h, HistoryField.TempC, At(13, 30, 30), At(13, 0), At(14, 0)));        // 断开处取最近的（30 s 内）
        Assert.Null(CurveStats.At(h, HistoryField.TempC, At(13, 30, 30), At(13, 0), At(14, 0), maxGapS: 10));
        var buf = Array.Empty<ChartPoint>();
        int n = CurveStats.CopyRange(h, HistoryField.TempC, At(13, 10), At(13, 20), ref buf);
        Assert.Equal(61, n);
        Assert.Equal((ChartTime.Ms(At(13, 10)), 30.0), (buf[0].T, Math.Round(buf[0].V, 6)));
        Assert.Equal(40.0, Math.Round(buf[n - 1].V, 6));
        // 断开处是 NaN（曲线在那里断开）
        n = CurveStats.CopyRange(h, HistoryField.TempC, At(13, 29), At(13, 32), ref buf);
        Assert.Contains(Enumerable.Range(0, n), i => double.IsNaN(buf[i].V));
    }

    [Fact]
    public void Rsd_SampleStdOverMean()
    {
        // 样稿：1号机已选 6 路的吸气量 18.4 / 2.1 / 12.7 / 15.9 / 21.3 / 16.2 → 46.3 %
        Assert.Equal(46.3, Math.Round(CurveStats.Rsd(new[] { 18.4, 2.1, 12.7, 15.9, 21.3, 16.2 })!.Value, 1));
        Assert.Null(CurveStats.Rsd(new[] { 1.0 }));
        Assert.Null(CurveStats.Rsd(new[] { 0.0, 0.0 }));
    }

    [Fact]
    public void Csv_AlignsByCell()
    {
        var h1 = Hist();
        var h2 = new ChannelHistory();
        h2.Append(At(13, 0, 3), 99, 1.234);
        var csv = CurveStats.Csv(new[] { new CurveStats.CsvChannel(1, h1), new CurveStats.CsvChannel(2, h2), new CurveStats.CsvChannel(3, null) },
            new[] { HistoryField.TempC, HistoryField.PressureMPa }, At(13, 0), At(13, 0, 20)).Split('\n');
        Assert.Equal("时间,RV1 温度 °C,RV1 压力 MPa,RV2 温度 °C,RV2 压力 MPa,RV3 温度 °C,RV3 压力 MPa", csv[0]);
        Assert.Equal("2026-11-15 13:00:00,20.0,0.000,99.0,1.234,,", csv[1]);
        Assert.StartsWith("2026-11-15 13:00:10,20.2,0.002,,,,", csv[2]);
        Assert.Equal(5, csv.Length);   // 表头 + 3 行 + 末尾空
    }
}
