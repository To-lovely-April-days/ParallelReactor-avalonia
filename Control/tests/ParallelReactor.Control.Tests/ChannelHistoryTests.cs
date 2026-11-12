using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.History;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.History.ChannelHistory / DeviceHistory：演示数据同步后与 DemoDevice.Series（样稿 series，已由 DemoFleetTests 对照样稿）逐点相同（多种场景、
/// 每秒推进时增量同步、参数变化重建历史）；实时帧按 10 s 一格（同一格留最后一帧）、断线插断开标记、读数缺失断开、时钟回跳、只留 4 h、二分查找、复用缓冲。
/// </summary>
public class ChannelHistoryTests
{
    private static readonly (HistoryField H, DemoReading D)[] Fields =
    {
        (HistoryField.TempC, DemoReading.TempC), (HistoryField.PressureMPa, DemoReading.PressureMPa), (HistoryField.HeaterPct, DemoReading.HeaterPct),
        (HistoryField.Rpm, DemoReading.Rpm), (HistoryField.Mmol, DemoReading.Mmol), (HistoryField.SpC, DemoReading.SpC), (HistoryField.PTargetMPa, DemoReading.PTargetMPa),
    };

    private static void SameAsDemo(DemoFleet f, IReadOnlyDictionary<int, DeviceHistory> books, string tag)
    {
        foreach (var d in f.Devices)
            foreach (var ch in d.Channels.Where(c => c.Installed))
                foreach (var (hf, df) in Fields)
                    foreach (double min in new[] { 10.0, 30, 60, 240 })
                    {
                        var want = d.Series(ch.N, df, min);
                        var got = books[d.K].Series(ch.N, hf, f.Now, min);
                        Assert.True(want.Count == got.Count, $"{tag} {d.Alias} RV{ch.N} {hf} {min} min：点数 {got.Count} ≠ {want.Count}");
                        for (int i = 0; i < want.Count; i++)
                            Assert.True(want[i].T == got[i].T && Nullable.Equals(want[i].V, got[i].V), $"{tag} {d.Alias} RV{ch.N} {hf} {min} min 第 {i} 点：{got[i]} ≠ {want[i]}");
                    }
    }

    [Theory]
    [InlineData("freeze=1")]
    [InlineData("freeze=1&stress=1")]
    [InlineData("freeze=1&devs=4&stress=2")]
    [InlineData("freeze=1&devs=PR8,PR4,PR16&offline=none")]
    [InlineData("freeze=1&offline=1,2&estop=3")]
    public void Demo_SyncMatchesDemoSeries(string query)
    {
        var f = new DemoFleet(DemoParams.Parse(query));
        var books = f.Devices.ToDictionary(d => d.K, d => new DeviceHistory());
        foreach (var d in f.Devices) books[d.K].SyncDemo(d);
        SameAsDemo(f, books, query);
        // 再同步一次不变
        int v = books[1].Channel(1).Version;
        foreach (var d in f.Devices) books[d.K].SyncDemo(d);
        Assert.Equal(v, books[1].Channel(1).Version);
        SameAsDemo(f, books, query + "（重复同步）");
    }

    [Fact]
    public void Demo_IncrementalWhileTicking_AndRebuildOnParams()
    {
        var f = new DemoFleet(DemoParams.Parse("devs=4"));
        var books = f.Devices.ToDictionary(d => d.K, d => new DeviceHistory());
        foreach (var d in f.Devices) books[d.K].SyncDemo(d);
        int n0 = books[1].Channel(1).Count;
        for (int t = 0; t < 125; t++)
        {
            f.Tick();
            foreach (var d in f.Devices) books[d.K].SyncDemo(d);
        }
        Assert.Equal(n0 + 12, books[1].Channel(1).Count);   // 125 s → 12 个新采样（每 10 s 一个）
        SameAsDemo(f, books, "推进 125 s");
        // 改参数（断线 / 恢复）会重建部分设备的历史：同步后仍与演示数据相同
        f.Apply(DemoParams.Parse("devs=4&offline=none"));
        foreach (var d in f.Devices) books[d.K].SyncDemo(d);
        SameAsDemo(f, books, "改参数后");
        f.Reset();
        foreach (var d in f.Devices) books[d.K].SyncDemo(d);
        SameAsDemo(f, books, "重置后");
    }

    private static readonly DateTime U0 = new(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Live_OneSamplePerCell_LastFrameWins()
    {
        var h = new ChannelHistory();
        // 每 1.2 s 一帧，共 60 s（50 帧）：落在 6 或 7 个 10 s 格里，每格一个采样，时刻 = 格里最后一帧
        for (int i = 0; i < 50; i++) h.Append(U0.AddSeconds(i * 1.2), 100 + i, 0.1 + i * 0.001);
        var cells = Enumerable.Range(0, 50).Select(i => U0.AddSeconds(i * 1.2)).GroupBy(t => t.Ticks / (10 * TimeSpan.TicksPerSecond)).ToArray();
        Assert.Equal(cells.Length, h.Count);
        for (int c = 0; c < cells.Length; c++)
        {
            var last = cells[c].Last();
            Assert.Equal(last, h[c].T);
            int i = (int)Math.Round((last - U0).TotalSeconds / 1.2);
            Assert.Equal(100.0 + i, h[c].TempC);
        }
        Assert.Equal(U0.AddSeconds(49 * 1.2), h.Last!.Value.T);   // 曲线末端 = 最新一帧
    }

    [Fact]
    public void Live_GapsBreakTheLine()
    {
        var h = new ChannelHistory();
        h.Append(U0, 50, 0.1);
        h.Append(U0.AddSeconds(10), 51, 0.1);
        h.Append(U0.AddSeconds(55), 52, 0.1);            // 45 s 没有数据（> 30 s）：先插断开标记
        Assert.Equal(4, h.Count);
        Assert.True(h[2].Gap && h[2].TempC is null);
        Assert.True(h[2].T > h[1].T && h[2].T < h[3].T);
        h.Append(U0.AddSeconds(70), null, 0.1);          // 温度缺失：这一点温度为空（曲线断开），压力照常
        Assert.Null(h[4].TempC);
        Assert.Equal(0.1, h[4].PressureMPa);
        h.MarkGap(U0.AddSeconds(71));                     // 断线：主动断开
        h.MarkGap(U0.AddSeconds(72));                     // 连续的断开标记只留一个
        Assert.Equal(6, h.Count);
        Assert.True(h[5].Gap);
        h.Append(U0.AddSeconds(300), 60, 0.2);           // 断开之后不再补第二个标记
        Assert.Equal(7, h.Count);
        var pts = h.Points(HistoryField.TempC, U0.AddSeconds(300), 60);
        Assert.Equal(new[] { true, true, false, true, false, false, true }, pts.Select(p => p.Ok).ToArray());
    }

    [Fact]
    public void Live_ClockJumpBack_DropsNewerPoints_KeepsOrder()
    {
        var h = new ChannelHistory();
        for (int i = 0; i < 10; i++) h.Append(U0.AddSeconds(i * 10), i, 0);
        h.Append(U0.AddSeconds(55), 99, 0);               // 时钟往回调：60..90 s 的点丢掉；55 s 与 50 s 同一格，替换它
        Assert.Equal(6, h.Count);
        Assert.Equal(99, h[5].TempC);
        for (int i = 1; i < h.Count; i++) Assert.True(h[i].T > h[i - 1].T);
    }

    [Fact]
    public void Live_KeepsFourHours_AndCapacity()
    {
        var h = new ChannelHistory();
        for (int i = 0; i <= 6 * 360; i++) h.Append(U0.AddSeconds(i * 10), i, 0);   // 6 h
        Assert.Equal(240 * 6 + 1, h.Count);              // 只留最近 240 min（含两端）
        Assert.Equal(U0.AddHours(2), h[0].T);
        var small = new ChannelHistory(capacity: 20);
        for (int i = 0; i < 50; i++) small.Append(U0.AddSeconds(i * 10), i, 0);
        Assert.Equal(20, small.Count);                   // 容量满了盖掉最旧的
        Assert.Equal(30, small[0].TempC);
        Assert.Equal(49, small[19].TempC);
    }

    [Fact]
    public void Live_WindowsAndBinarySearch()
    {
        var h = new ChannelHistory();
        for (int i = 0; i < 400; i++) h.Append(U0.AddSeconds(i * 10), i, i / 100.0);
        var now = U0.AddSeconds(3990);
        Assert.Equal(61, h.Series(HistoryField.TempC, now, 10).Count);      // ≥ now − 10 min：600 s / 10 + 1
        Assert.Equal(181, h.Series(HistoryField.TempC, now, 30).Count);
        Assert.Equal(361, h.Series(HistoryField.TempC, now, 60).Count);
        Assert.Equal(361, h.Series(HistoryField.TempC, now, 0).Count);       // 0 按 60
        Assert.Equal(DateTimeKind.Local, h.Series(HistoryField.TempC, now, 10)[0].T.Kind);
        Assert.Equal(0, h.LowerBound(U0.AddSeconds(-5)));
        Assert.Equal(1, h.LowerBound(U0.AddSeconds(5)));
        Assert.Equal(1, h.LowerBound(U0.AddSeconds(10)));
        Assert.Equal(400, h.LowerBound(U0.AddSeconds(5000)));
        Assert.Equal(0, h.Nearest(U0.AddSeconds(5)));      // 一样近取前一个
        Assert.Equal(1, h.Nearest(U0.AddSeconds(6)));
        Assert.Equal(399, h.Nearest(U0.AddSeconds(9999)));
        Assert.Equal(-1, new ChannelHistory().Nearest(U0));
        // 复用缓冲
        var buf = Array.Empty<ChartPoint>();
        int n = h.CopyPoints(HistoryField.PressureMPa, now, 30, ref buf);
        var keep = buf;
        Assert.Equal(181, n);
        Assert.Equal(61, h.CopyPoints(HistoryField.PressureMPa, now, 10, ref buf));
        Assert.Same(keep, buf);
        Assert.Equal(ChartTime.Ms(U0.AddSeconds(3390)), buf[0].T);
        Assert.Equal(3.39, buf[0].V, 10);
    }

    [Fact]
    public void Live_AppendFrame_MapsSnapshot()
    {
        var dh = new DeviceHistory();
        var f = Frame(U0, true, 800,
            Ch(1, temp: 118.4, sp: 120, psi: 50.76, pSp: 50.76, heat: 63, state: "heating", atMode: 0),
            Ch(2, temp: 25.1, sp: 120, psi: 0.3, heat: 0, state: "ready"),
            Ch(5, installed: false));
        dh.AppendFrame(f);
        var a = dh.Channel(1).Last!.Value;
        Assert.Equal(118.4, a.TempC);
        Assert.Equal(Units.PsiToMpa(50.76), a.PressureMPa);
        Assert.Equal(63, a.HeaterPct);
        Assert.Equal(800, a.Rpm);
        Assert.Equal(120, a.SpC);                              // 加热中才记设定值
        Assert.Equal(Units.PsiToMpa(50.76), a.PTargetMPa);
        var b = dh.Channel(2).Last!.Value;
        Assert.Null(b.SpC);                                    // At = 4（不加热）：没有设定值
        Assert.Null(b.PTargetMPa);
        Assert.False(dh.Has(5));                               // 未安装不记
        dh.AppendFrame(Frame(U0.AddSeconds(10), false, 800, Ch(1, temp: 119, atMode: 0, sp: 120)));
        Assert.Equal(0, dh.Channel(1).Last!.Value.Rpm);        // 搅拌关着：0
        dh.MarkGap(U0.AddSeconds(11));
        Assert.True(dh.Channel(1).Last!.Value.Gap && dh.Channel(2).Last!.Value.Gap);
        Assert.Equal(3, dh.Series(1, HistoryField.TempC, U0.AddSeconds(11)).Count);
        Assert.Empty(dh.Series(7, HistoryField.TempC, U0));
        dh.Clear();
        Assert.False(dh.Has(1));
    }
}
