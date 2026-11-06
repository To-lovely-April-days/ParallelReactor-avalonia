using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>DataLogger 落盘：BOM、表头只写一次、标记 / 会话、表头不一致另起文件、写盘失败不抛出。</summary>
public class DataLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pr-logger-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.FromHours(8));

    private static TelemetryRow Row(DateTimeOffset t) => new() { Time = t };

    private string TelPath(DateTimeOffset t) => Path.Combine(_dir, $"telemetry-{t:yyyyMMdd}.csv");

    [Fact]
    public async Task Telemetry_WritesBomAndHeaderOnce_AppendsAcrossRestarts()
    {
        var log = new DataLogger(_dir);
        log.Telemetry(Row(T0));
        log.Telemetry(Row(T0.AddSeconds(1.2)));
        await log.StopAsync();

        var log2 = new DataLogger(_dir);   // 程序重启后追加到同一个日文件
        log2.Telemetry(Row(T0.AddSeconds(2.4)));
        await log2.StopAsync();

        var bytes = File.ReadAllBytes(TelPath(T0));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal(1, CountBom(bytes));   // 追加时不再写 BOM
        var lines = File.ReadAllLines(TelPath(T0), Encoding.UTF8);
        Assert.Equal(4, lines.Length);
        Assert.Equal(TelemetryCsv.Header, lines[0]);
        Assert.Equal(1, lines.Count(l => l == TelemetryCsv.Header));
    }

    [Fact]
    public async Task Telemetry_RollsDaily_ByRowTimestamp()
    {
        var log = new DataLogger(_dir);
        var late = new DateTimeOffset(2026, 11, 6, 23, 59, 59, TimeSpan.FromHours(8));
        log.Telemetry(Row(late));
        log.Telemetry(Row(late.AddSeconds(2)));
        await log.StopAsync();
        Assert.Equal(2, File.ReadAllLines(TelPath(late)).Length);
        Assert.Equal(2, File.ReadAllLines(TelPath(late.AddSeconds(2))).Length);
    }

    [Fact]
    public async Task HeaderMismatch_StartsNewFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TelPath(T0), "旧,表头\n1,2\n", new UTF8Encoding(true));
        var log = new DataLogger(_dir);
        log.Telemetry(Row(T0));
        await log.StopAsync();
        Assert.Equal("1,2", File.ReadAllLines(TelPath(T0))[1]);   // 旧文件不动
        var alt = Path.Combine(_dir, $"telemetry-{T0:yyyyMMdd}-1.csv");
        Assert.Equal(TelemetryCsv.Header, File.ReadAllLines(alt)[0]);
    }

    [Fact]
    public async Task Marker_IsConsumedByNextRowOnly_AndSessionWritesSecondFile()
    {
        var log = new DataLogger(_dir);
        var sessionPath = log.BeginSession("T 01/a", T0);
        log.AddMarker("开始升温");
        log.Telemetry(Row(T0));
        log.Telemetry(Row(T0.AddSeconds(1)));
        log.EndSession();
        log.Telemetry(Row(T0.AddSeconds(2)));
        await log.StopAsync();

        Assert.EndsWith("T_01_a-20261106-100000.csv", sessionPath);
        var daily = File.ReadAllLines(TelPath(T0)).Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(3, daily.Count);
        Assert.Equal("开始升温", daily[0][1]);
        Assert.Equal("T 01/a", daily[0][2]);
        Assert.Equal("", daily[1][1]);
        Assert.Equal("", daily[2][2]);   // 会话已结束
        var ses = File.ReadAllLines(sessionPath);
        Assert.Equal(3, ses.Length);     // 表头 + 2 行
        Assert.Equal(TelemetryCsv.Header, ses[0]);
    }

    [Fact]
    public async Task Events_AndWriteFile()
    {
        var log = new DataLogger(_dir);
        log.Event(T0, "info", "系统", "程序启动", "a,b");
        log.WriteFile("leak/x.csv", "h\n1\n");
        await log.FlushAsync();
        var ev = File.ReadAllLines(Path.Combine(_dir, $"events-{T0:yyyyMMdd}.csv"));
        Assert.Equal(TelemetryCsv.EventHeader, ev[0]);
        Assert.Equal("2026-11-06T10:00:00.000+08:00,info,系统,程序启动,\"a,b\"", ev[1]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(Path.Combine(_dir, "leak", "x.csv")).Take(3));
        await log.StopAsync();
    }

    [Fact]
    public async Task WriteFailure_DoesNotThrow_AndReportsOnce()
    {
        File.WriteAllText(_dir, "这是一个文件，不是目录");   // 数据目录无法创建
        int reports = 0;
        var log = new DataLogger(_dir, _ => reports++);
        log.Telemetry(Row(T0));
        log.Event(T0, "info", "x", "y", "z");
        log.Telemetry(Row(T0.AddSeconds(1)));
        await log.FlushAsync();
        await log.StopAsync();
        Assert.Equal(1, reports);
        Assert.True(log.DroppedLines >= 2);
        File.Delete(_dir);
    }

    [Fact]
    public async Task EndSession_WithFinalRow_PutsEndMarkerInSessionFile()
    {
        var log = new DataLogger(_dir);
        var sessionPath = log.BeginSession("S1", T0);
        log.Telemetry(Row(T0));
        log.AddMarker("会话结束 S1");   // 日运行记录：下一行
        log.EndSession(Row(T0.AddSeconds(0.5)), "会话结束 S1");
        log.Telemetry(Row(T0.AddSeconds(1.2)));
        await log.StopAsync();

        var ses = File.ReadAllLines(sessionPath).Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(2, ses.Count);
        Assert.Equal("会话结束 S1", ses[1][1]);
        Assert.Equal("S1", ses[1][2]);
        var daily = File.ReadAllLines(TelPath(T0)).Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(2, daily.Count);   // 会话最后一行只写会话文件
        Assert.Equal("会话结束 S1", daily[1][1]);
        Assert.Equal("", daily[1][2]);
    }

    [Fact]
    public async Task Append_AfterCrashLeftPartialLine_StartsOnNewLine()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TelPath(T0), TelemetryCsv.Header + "\n2026-11-06T09:59:59.000+08:00,半行", new UTF8Encoding(true));
        var log = new DataLogger(_dir);
        log.Telemetry(Row(T0));
        await log.StopAsync();
        var lines = File.ReadAllLines(TelPath(T0));
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",半行", lines[1]);
        Assert.Equal(TelemetryCsv.Format(Row(T0)), lines[2]);   // 新行完整、列数正确
    }

    private static int CountBom(byte[] b)
    {
        int n = 0;
        for (int i = 0; i + 2 < b.Length; i++)
            if (b[i] == 0xEF && b[i + 1] == 0xBB && b[i + 2] == 0xBF) n++;
        return n;
    }
}
