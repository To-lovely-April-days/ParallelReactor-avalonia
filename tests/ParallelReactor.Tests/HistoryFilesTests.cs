using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>历史文件：日文件选择、[from,to) 过滤、半行跳过、表头只写一次（设计稿 §9）。</summary>
public class HistoryFilesTests
{
    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "pr-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static readonly TimeSpan Off = TimeSpan.FromHours(8);
    private static DateTimeOffset L(string s) => DateTimeOffset.Parse(s + "+08:00");
    /// <summary>本机时区的时刻（日文件按本机本地日期滚动，选择文件的测试必须用本机时区才不依赖运行机器）。</summary>
    private static DateTimeOffset LL(string s) => new(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.Local.GetUtcOffset(DateTime.Parse(s)));

    /// <summary>写一个 telemetry 日文件：表头 + 每个时刻一行（本地 +08:00）。tail 不以换行结束时模拟半行。</summary>
    private static string WriteDay(string dir, string name, IEnumerable<DateTimeOffset> times, string? halfLine = null)
    {
        var sb = new StringBuilder();
        sb.Append("时间,标记,会话,RV1_PV(℃)\n");
        foreach (var t in times) sb.Append(Csv.Time(t)).Append(",,,25.0\n");
        if (halfLine != null) sb.Append(halfLine);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, sb.ToString(), new UTF8Encoding(true));
        return p;
    }

    [Fact]
    public void DayOf_ParsesNameAndSuffix()
    {
        Assert.Equal(("20261106", 0), HistoryFiles.DayOf("telemetry-20261106.csv")!.Value);
        Assert.Equal(("20261106", 2), HistoryFiles.DayOf("telemetry-20261106-2.csv")!.Value);
        Assert.Equal(("20261107", 0), HistoryFiles.DayOf("events-20261107.jsonl")!.Value);
        Assert.Null(HistoryFiles.DayOf("settings.json"));
    }

    [Fact]
    public void SelectDailyFiles_LocalDateWindow_IncludesSuffixFiles_InOrder()
    {
        var dir = Tmp();
        WriteDay(dir, "telemetry-20261105.csv", new[] { L("2026-11-05T23:59:00") });
        WriteDay(dir, "telemetry-20261106.csv", new[] { L("2026-11-06T00:00:30") });
        WriteDay(dir, "telemetry-20261106-1.csv", new[] { L("2026-11-06T12:00:00") });
        WriteDay(dir, "telemetry-20261107.csv", new[] { L("2026-11-07T00:00:30") });
        WriteDay(dir, "telemetry-20261108.csv", new[] { L("2026-11-08T00:00:30") });

        // 跨本地日界：06 日 13:00 → 07 日 11:00（本机本地时间）：应取 06（含 -1 后缀）与 07，排除 05 / 08
        var files = HistoryFiles.SelectDailyFiles(dir, LL("2026-11-06T13:00:00"), LL("2026-11-07T11:00:00"));
        var names = files.Select(Path.GetFileName).ToList();
        Assert.Contains("telemetry-20261106.csv", names);
        Assert.Contains("telemetry-20261106-1.csv", names);
        Assert.Contains("telemetry-20261107.csv", names);
        Assert.DoesNotContain("telemetry-20261105.csv", names);
        Assert.DoesNotContain("telemetry-20261108.csv", names);
        Assert.True(names.IndexOf("telemetry-20261106.csv") < names.IndexOf("telemetry-20261106-1.csv"));
        Assert.True(names.IndexOf("telemetry-20261106-1.csv") < names.IndexOf("telemetry-20261107.csv"));

        Assert.Empty(HistoryFiles.SelectDailyFiles(dir, LL("2026-11-07T00:00:00"), LL("2026-11-07T00:00:00")));   // 空区间
        // to 恰为 07 日 00:00（半开区间）：不取 07 日文件
        Assert.Equal(new[] { "telemetry-20261106.csv", "telemetry-20261106-1.csv" },
            HistoryFiles.SelectDailyFiles(dir, LL("2026-11-06T01:00:00"), LL("2026-11-07T00:00:00")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WriteRange_HalfOpen_HeaderOnce_SkipsHalfLine()
    {
        var dir = Tmp();
        var f1 = WriteDay(dir, "telemetry-20261106.csv", new[] { L("2026-11-06T23:59:58.800"), L("2026-11-06T23:59:59.999") });
        var f2 = WriteDay(dir, "telemetry-20261107.csv",
            new[] { L("2026-11-07T00:00:01.200"), L("2026-11-07T00:00:02.400"), L("2026-11-07T00:00:03.600") },
            halfLine: "2026-11-07T00:00:04.800+08:00,,,2");   // 断电留下的半行

        using var ms = new MemoryStream();
        long rows = await HistoryFiles.WriteRangeAsync(new[] { f1, f2 }, L("2026-11-06T23:59:59.999"), L("2026-11-07T00:00:03.600"), ms, CancellationToken.None);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        Assert.StartsWith("﻿时间,", text);   // BOM + 表头一次
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(1 + 3, lines.Length);
        Assert.Equal(3, rows);
        Assert.Contains("2026-11-06T23:59:59.999+08:00", lines[1]);   // from 含
        Assert.Contains("2026-11-07T00:00:02.400+08:00", lines[3]);
        Assert.DoesNotContain("00:00:03.600", text);   // to 不含
        Assert.DoesNotContain("00:00:04.800", text);   // 半行不输出
        Assert.Equal(1, text.Split("时间,").Length - 1);
    }

    [Fact]
    public async Task WriteRange_NoFiles_WritesOnlyBom()
    {
        using var ms = new MemoryStream();
        long rows = await HistoryFiles.WriteRangeAsync(Array.Empty<string>(), L("2026-11-06T00:00:00"), L("2026-11-07T00:00:00"), ms, CancellationToken.None);
        Assert.Equal(0, rows);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, ms.ToArray());
    }

    [Fact]
    public void FirstLastTimestamp_SkipsHalfLine()
    {
        var dir = Tmp();
        var f = WriteDay(dir, "telemetry-20261106.csv", new[] { L("2026-11-06T08:00:00"), L("2026-11-06T08:00:01.200") }, halfLine: "2026-11-06T08:00:02.4");
        var (from, to) = HistoryFiles.FirstLastTimestamp(f);
        Assert.Equal(new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 11, 6, 0, 0, 1, 200, DateTimeKind.Utc), to);

        var empty = WriteDay(dir, "telemetry-20261107.csv", Array.Empty<DateTimeOffset>());
        Assert.Equal((null, null), HistoryFiles.FirstLastTimestamp(empty));
    }

    [Fact]
    public void List_FiltersByTypeAndValidName()
    {
        var dir = Tmp();
        WriteDay(dir, "telemetry-20261106.csv", new[] { L("2026-11-06T08:00:00") });
        File.WriteAllText(Path.Combine(dir, "telemetry-bad.csv"), "x");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "events"));
        File.WriteAllText(Path.Combine(dir, "events", "events-20261106.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(dir, "events-20261106.csv"), "时间\n");
        var tel = HistoryFiles.List(dir, SafePath.Telemetry);
        Assert.Single(tel);
        Assert.Equal("telemetry-20261106.csv", tel[0].Name);
        Assert.Equal("20261106", tel[0].Day);
        Assert.NotNull(tel[0].From);
        var ev = HistoryFiles.List(dir, SafePath.Events).Select(x => x.Name).ToList();
        Assert.Equal(new[] { "events-20261106.csv", "events-20261106.jsonl" }, ev);
        Assert.Empty(HistoryFiles.List(dir, SafePath.Leak));
        Assert.Empty(HistoryFiles.List(dir, "bogus"));
    }

    [Theory]
    [InlineData("2026-11-06T08:00:00Z", true)]
    [InlineData("2026-11-06T16:00:00+08:00", true)]
    [InlineData("2026-11-06T16:00:00.123-0500", true)]
    [InlineData("2026-11-06T08:00:00", false)]
    [InlineData("2026-11-06", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("yesterday", false)]
    public void TryParseQueryTime_RequiresZone(string? s, bool ok)
        => Assert.Equal(ok, HistoryFiles.TryParseQueryTime(s, out _));

    [Fact]
    public void ParseRowTime_HandlesQuotedAndBad()
    {
        Assert.Equal(L("2026-11-06T08:00:00.120"), HistoryFiles.ParseRowTime("2026-11-06T08:00:00.120+08:00,a,b"));
        Assert.Equal(L("2026-11-06T08:00:00.120"), HistoryFiles.ParseRowTime("\"2026-11-06T08:00:00.120+08:00\",a"));
        Assert.Null(HistoryFiles.ParseRowTime("时间,标记"));
        Assert.Null(HistoryFiles.ParseRowTime(""));
    }

    [Fact]
    public void ParseSessionName()
    {
        var p = HistoryFiles.ParseSessionName("TP-017-20261106-070000.csv");
        Assert.NotNull(p);
        Assert.Equal("TP-017", p!.Value.TestId);
        Assert.Equal(new DateTime(2026, 11, 6, 7, 0, 0), p.Value.StartedLocal);
        Assert.Null(HistoryFiles.ParseSessionName("nope.csv"));
    }

    [Fact]
    public void SplitCsv_ColumnMap_Remap()
    {
        Assert.Equal(new[] { "a", "", "x,y", "q\"r", "" }, HistoryFiles.SplitCsv("a,,\"x,y\",\"q\"\"r\","));
        var target = new[] { "时间", "标记", "会话", "RV1_PV(℃)" };
        Assert.Null(HistoryFiles.ColumnMap(target, (string[])target.Clone()));
        var map = HistoryFiles.ColumnMap(target, new[] { "时间", "标记", "RV1_PV(℃)", "会话", "新列" });
        Assert.Equal(new[] { 0, 1, 3, 2 }, map);
        Assert.Equal("2026-11-06T08:00:00.000+08:00,,S1,25.5", HistoryFiles.Remap("2026-11-06T08:00:00.000+08:00,,25.5,S1,extra", map!));
        var map2 = HistoryFiles.ColumnMap(target, new[] { "时间", "RV1_PV(℃)" });
        Assert.Equal(new[] { 0, -1, -1, 1 }, map2);
        Assert.Equal("t,,,\"a,b\"", HistoryFiles.Remap("t,\"a,b\"", map2!));   // 缺列留空、含逗号的值重新加引号
    }

    [Fact]
    public async Task WriteRange_MapsColumnsByName_WhenHeaderChanges()
    {
        // P0 表头变化另起 -1 文件：第二个文件列序不同、多一列，输出按第一个表头的列名重排（§9.3 按表头解析，不按列位置）
        var dir = Tmp();
        var f1 = WriteDay(dir, "telemetry-20261106.csv", new[] { L("2026-11-06T08:00:00") });
        var f2 = Path.Combine(dir, "telemetry-20261106-1.csv");
        File.WriteAllText(f2, "时间,标记,RV1_PV(℃),会话,新列\n" + Csv.Time(L("2026-11-06T08:00:01.200")) + ",,26.5,S1,zzz\n", new UTF8Encoding(true));
        using var ms = new MemoryStream();
        long rows = await HistoryFiles.WriteRangeAsync(new[] { f1, f2 }, L("2026-11-06T00:00:00"), L("2026-11-07T00:00:00"), ms, CancellationToken.None);
        Assert.Equal(2, rows);
        var lines = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("时间,标记,会话,RV1_PV(℃)", lines[0]);
        Assert.Equal(Csv.Time(L("2026-11-06T08:00:00")) + ",,,25.0", lines[1]);
        Assert.Equal(Csv.Time(L("2026-11-06T08:00:01.200")) + ",,S1,26.5", lines[2]);
    }

    [Fact]
    public void LastCompleteLine_TailRead_MatchesFullScan_AndCacheRefreshes()
    {
        var dir = Tmp();
        // 只有表头（整文件在一块里，带 BOM）→ 末行是表头 → (null, null)
        var onlyHeader = WriteDay(dir, "telemetry-20261105.csv", Array.Empty<DateTimeOffset>());
        Assert.Equal("时间,标记,会话,RV1_PV(℃)", HistoryFiles.LastCompleteLine(onlyHeader));
        Assert.Equal((null, null), HistoryFiles.ComputeFirstLast(onlyHeader));

        // 大文件（> 64 KB 尾块）+ 半行：尾读与整文件扫一致
        var times = Enumerable.Range(0, 4000).Select(i => L("2026-11-06T08:00:00").AddMilliseconds(1200 * i)).ToList();
        var big = WriteDay(dir, "telemetry-20261106.csv", times, halfLine: "2026-11-06T09:20:00.0");
        Assert.True(new FileInfo(big).Length > HistoryFiles.TailChunk);
        Assert.Equal(Csv.Time(times[^1]) + ",,,25.0", HistoryFiles.LastCompleteLine(big));
        var (from, to) = HistoryFiles.FirstLastTimestamp(big);
        Assert.Equal(times[0].UtcDateTime, from);
        Assert.Equal(times[^1].UtcDateTime, to);

        // 追加一行后长度变了：缓存失效，结果更新
        var extra = L("2026-11-06T09:30:00");
        File.AppendAllText(big, "\n" + Csv.Time(extra) + ",,,25.0\n");
        File.SetLastWriteTimeUtc(big, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(extra.UtcDateTime, HistoryFiles.FirstLastTimestamp(big).To);
    }
}
