using System;
using ParallelReactor.Control.Core.History;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 c1-live-3（Core 部分）：断开标记的时刻是本机时钟换算的「现在」，时钟差不对（或断开后不知道）时可能差几个小时 ——
/// 以前 4 h 的保留按标记的时刻裁，HMI 的钟慢 8 h 时一个标记就把曲线全裁掉；钟慢 30 min 时裁掉最早的 30 min。
/// 现在标记夹在 [最后一个采样, 最后一个采样 + GapSeconds]、断开标记不触发保留的裁剪；之后的真实帧照常（标记留着，曲线在这里断开）。
/// </summary>
public class M5ReviewHistoryTests
{
    private static readonly DateTime PcNow = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    private static DeviceHistory Filled(DateTime devNow, int minutes)
    {
        var h = new DeviceHistory();
        for (int i = minutes * 6 - 1; i >= 0; i--)
            h.AppendFrame(Frame(devNow - TimeSpan.FromSeconds(10 * i), Ch(1, 80, 80, 300, state: "heating")));
        return h;
    }

    /// <summary>复核的探针：HMI 慢 8 h（2 h / 4 h 的数据）、慢 30 min（满 4 h）—— MarkGap(本机的现在) 之后一个采样都不少。</summary>
    [Theory]
    [InlineData(8.0, 120)]
    [InlineData(8.0, 240)]
    [InlineData(0.5, 240)]
    public void MarkGap_WithAWrongClock_KeepsTheHistory(double lagHours, int minutes)
    {
        var devNow = PcNow - TimeSpan.FromHours(lagHours);
        var h = Filled(devNow, minutes);
        var ch = h.Channel(1);
        int before = ch.Count;
        var oldest = ch[0].T;
        Assert.Equal(minutes * 6, before);
        h.MarkGap(PcNow);
        Assert.Equal(before + 1, ch.Count);
        Assert.Equal(oldest, ch[0].T);
        var gap = ch[ch.Count - 1];
        Assert.True(gap.Gap);
        Assert.Equal(devNow + TimeSpan.FromSeconds(ch.GapSeconds), gap.T);                // 夹在最后一个采样 + 30 s
        // 重连之后的下一帧（设备时钟）：标记留着（不再走「时钟回跳」被删），曲线在这里断开；保留照常按读数的时刻裁
        var next = devNow + TimeSpan.FromMinutes(1);
        h.AppendFrame(Frame(next, Ch(1, 80, 80, 300, state: "heating")));
        Assert.True(ch[ch.Count - 2].Gap);
        Assert.Equal(next, ch[ch.Count - 1].T);
        Assert.True(ch[0].T >= next - TimeSpan.FromMinutes(ChannelHistory.KeepMinutes));
        Assert.True(ch.Count >= before - 6);                                              // 4 h 满的：只裁掉新帧往前 4 h 之外的那几个
    }

    /// <summary>时钟差对的时候照旧：标记在断开的那一刻；比最后一个采样早时夹到最后一个采样；断开标记本身从不裁剪。</summary>
    [Fact]
    public void MarkGap_Clamp_BothSides_NoRetentionCut()
    {
        var devNow = PcNow - TimeSpan.FromHours(3);
        var h = Filled(devNow, 60);
        var ch = h.Channel(1);
        h.MarkGap(devNow + TimeSpan.FromSeconds(12));
        Assert.Equal(devNow + TimeSpan.FromSeconds(12), ch[ch.Count - 1].T);
        var c2 = Filled(devNow, 60).Channel(1);
        c2.MarkGap(devNow - TimeSpan.FromHours(5));                                      // 钟快：夹到最后一个采样
        Assert.Equal((devNow, true), (c2[c2.Count - 1].T, c2[c2.Count - 1].Gap));
        // 直接追加一个很晚的断开标记（不经 MarkGap）：也不裁
        var c3 = Filled(devNow, 60).Channel(1);
        int n = c3.Count;
        c3.Append(new HistorySample(devNow + TimeSpan.FromHours(9), null, null, null, null, null, null, null, Gap: true));
        Assert.Equal(n + 1, c3.Count);
    }
}
