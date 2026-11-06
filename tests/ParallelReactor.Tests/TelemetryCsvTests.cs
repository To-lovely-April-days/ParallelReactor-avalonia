using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>运行记录 CSV：字段转义、数值格式、表头稳定性（纯逻辑）。</summary>
public class TelemetryCsvTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    [InlineData("中文标记", "中文标记")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("cr\rhere", "\"cr\rhere\"")]
    [InlineData("=1+1", "'=1+1")]                 // 防 Excel 公式注入
    [InlineData("-0.02 psi", "'-0.02 psi")]
    [InlineData("@x", "'@x")]
    [InlineData("+a,b", "\"'+a,b\"")]
    [InlineData("-1.5", "-1.5")]                  // 纯数字不动
    [InlineData("+3e2", "+3e2")]
    public void Escape_QuotesOnlyWhenNeeded(string? input, string expected)
        => Assert.Equal(expected, Csv.Escape(input));

    [Fact]
    public void Num_IsInvariantCulture_AndEmptyForMissing()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");   // 小数点是逗号的区域
            Assert.Equal("1.5", Csv.Num(1.5));
            Assert.Equal("", Csv.Num(null));
            Assert.Equal("", Csv.Num(double.NaN));
            Assert.Equal("", Csv.Num(double.PositiveInfinity));
            Assert.Equal("", Csv.Int(null));
            Assert.Equal("1", Csv.Bool(true));
            Assert.Equal("", Csv.Bool(null));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }

    [Fact]
    public void Time_IsIso8601WithOffsetAndMilliseconds()
    {
        var t = new DateTimeOffset(2026, 11, 6, 14, 3, 21, 123, TimeSpan.FromHours(8));
        Assert.Equal("2026-11-06T14:03:21.123+08:00", Csv.Time(t));
        Assert.Equal("", Csv.Time(null));
    }

    [Fact]
    public void Header_IsStable()
    {
        var h = TelemetryCsv.HeaderFields().ToList();
        // 3 通用列 + 8 通道 × 15 列 + 12 全局列 + 4 模块 × 5 列 + 模拟标志。改动列必须同步改这里（且只能在末尾追加）。
        Assert.Equal(3 + 8 * 15 + 12 + 4 * 5 + 1, h.Count);
        Assert.Equal(new[] { "时间", "标记", "会话", "RV1_PV(℃)", "RV1_PV原码" }, h.Take(5));
        Assert.Equal("RV8_进气阀", h[3 + 8 * 15 - 1]);
        Assert.Equal("搅拌_最近响应", h[^2]);
        Assert.Equal("模拟", h[^1]);
        Assert.Equal(h.Count, h.Distinct().Count());   // 列名不重复
        Assert.Same(TelemetryCsv.Header, TelemetryCsv.Header);   // 缓存，每行不重建
        Assert.Equal(Csv.Line(h), TelemetryCsv.Header);
    }

    [Fact]
    public void Format_EmptyRow_HasSameColumnCountAsHeader_AndEmptyCellsForMissing()
    {
        var row = new TelemetryRow { Time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero) };
        var cells = row.Format().Split(',');
        Assert.Equal(TelemetryCsv.HeaderFields().Count(), cells.Length);
        Assert.Equal("2026-01-02T03:04:05.006+00:00", cells[0]);
        Assert.Equal("", cells[3]);   // RV1 PV 未知 → 空，不写 0
        Assert.Equal("", cells[4]);
    }

    [Fact]
    public void Format_FillsValues_AndQuotesMarker()
    {
        var row = new TelemetryRow { Time = DateTimeOffset.Now, Marker = "开始, 第1段", Session = "T01" };
        row.Ch[0].Pv = 25.4;
        row.Ch[0].PvRaw = 254;
        row.Ch[0].At = 4;
        row.Ch[0].PressPsi = 145.037738;
        row.Ch[0].State = "Idle";
        row.Temp = new TelemetryModule { Configured = true, Online = true, Polls = 10, Fails = 2 };
        var line = row.Format();
        Assert.Contains("\"开始, 第1段\",T01,25.4,254,", line);
        Assert.Contains(",4,", line);
        Assert.Contains(",1.00000,", line);   // 145.037738 psi = 1 MPa
        Assert.Contains(",1,1,10,2,", line);  // 温控：已配置 / 在线 / 事务 / 失败
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void EventLine_Format()
    {
        var t = new DateTimeOffset(2026, 11, 6, 9, 0, 0, 5, TimeSpan.FromHours(8));
        Assert.Equal("2026-11-06T09:00:00.005+08:00,err,RV1,超温 · 切断加热,\"PV 251.0℃, 限值 250℃\"",
            TelemetryCsv.FormatEvent(t, "err", "RV1", "超温 · 切断加热", "PV 251.0℃, 限值 250℃"));
        Assert.Matches(new Regex("^时间,级别,来源,标题,详情$"), TelemetryCsv.EventHeader);
    }
}

internal static class TelemetryRowExt
{
    public static string Format(this TelemetryRow r) => TelemetryCsv.Format(r);
}
