using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Pid;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 设置 → 温控 / PID 的自整定（Core）：档位的整定温度范围与原因、默认温度、档位卡片的文字、振荡计数（PV 穿越整定温度 / 输出通断）、
/// 整定前后的对比文字、演示数据（样稿 PIDS、整定曲线、3 次振荡后存入新 PID、取消 / 急停不改 PID）。
/// </summary>
public class PidTuneTests
{
    private static readonly PidBand Low = new(0, "低温档", 0, 100, false), Mid = new(1, "中温档", 100, 200, false), High = new(2, "高温档", 200, 400, true);

    // ———————————————————— 档位范围 ————————————————————

    [Fact]
    public void Range_BandIntersect40ToTmax_UpperBoundBelongsToNextBand()
    {
        var lo = PidTune.Range(Low, 200);
        Assert.Equal((40d, 100d, false, true), (lo.Min, lo.Max, lo.MaxInclusive, lo.Usable));
        Assert.True(lo.Contains(40));
        Assert.True(lo.Contains(99.9));
        Assert.False(lo.Contains(100));
        Assert.False(lo.Contains(39.9));
        Assert.Equal("40 – 100 °C", lo.Text);
        var mid = PidTune.Range(Mid, 200);
        Assert.Equal("100 – 200 °C", mid.Text);
        Assert.False(mid.Contains(200));
        // 本机上限低于档的上限：上限 = TMaxSp（含）
        var mid180 = PidTune.Range(Mid, 180);
        Assert.Equal((180d, true), (mid180.Max, mid180.MaxInclusive));
        Assert.True(mid180.Contains(180));
        // 最高档：[lo, TMaxSp]；下限 ≥ 上限 → 不能用（样稿「超过本机上限 200 °C」）
        Assert.False(PidTune.Range(High, 200).Usable);
        var hi = PidTune.Range(High, 300);
        Assert.Equal((200d, 300d, true, true), (hi.Min, hi.Max, hi.MaxInclusive, hi.Usable));
    }

    [Fact]
    public void TempReason_Texts()
    {
        Assert.Null(PidTune.TempReason(Mid, 200, 150));
        Assert.Null(PidTune.TempReason(Mid, 200, 100));
        Assert.Equal("整定温度不在本档区间内（100 – 200 °C）", PidTune.TempReason(Mid, 200, 250));
        Assert.Equal("整定温度不在本档区间内（100 – 200 °C）", PidTune.TempReason(Mid, 200, 99));
        Assert.Equal("整定温度须低于 200 °C（200 °C 属于下一档）", PidTune.TempReason(Mid, 200, 200));
        Assert.Equal("整定温度不在本档区间内（40 – 100 °C）", PidTune.TempReason(Low, 200, 30));
        Assert.Equal("先填整定温度", PidTune.TempReason(Mid, 200, null));
        Assert.Equal("先填整定温度", PidTune.TempReason(Mid, 200, double.NaN));
        Assert.Equal("超过本机温度上限", PidTune.TempReason(High, 200, 250));
    }

    [Fact]
    public void DefaultTemp_MockValues_OrMiddleOfTheRange()
    {
        Assert.Equal(80, PidTune.DefaultTemp(Low, 200));
        Assert.Equal(150, PidTune.DefaultTemp(Mid, 200));
        Assert.Equal(250, PidTune.DefaultTemp(High, 300));
        Assert.Equal(215, PidTune.DefaultTemp(High, 230));                          // 250 超过上限 → 中间
        Assert.Equal(125, PidTune.DefaultTemp(new PidBand(1, "中温档", 100, 150, false), 200));   // 150 属于下一档 → 中间
        Assert.Equal(2, PidTune.BandFor(new[] { Low, Mid, High }, 450));            // 超出最高档上限归最高档
        Assert.Equal(1, PidTune.BandFor(new[] { Low, Mid, High }, 100));
    }

    [Fact]
    public void BandStatus_Texts()
    {
        Assert.Equal("7 路已整定", PidTune.BandStatus(7, 7, true, 200));
        Assert.Equal("5/7 路有整定记录", PidTune.BandStatus(5, 7, true, 200));
        Assert.Equal("无整定记录", PidTune.BandStatus(0, 7, true, 200));   // 升级前整定过的现场设备：不说「未整定」
        Assert.Equal("超过本机上限 200 °C", PidTune.BandStatus(0, 7, false, 200));
    }

    // ———————————————————— 振荡计数 ————————————————————

    private static List<TuneSample> Curve(double upTo, double sp = 150, double t0 = 28, int n = 5, double step = 10 / 60.0, double jitter = 0, bool pv = true)
    {
        var rnd = new Random(7);
        var list = new List<TuneSample>();
        for (double m = 0; m <= upTo + 1e-9; m += step)
        {
            var (t, o) = PidDemoCurve.At(m, sp, t0, n);
            list.Add(new TuneSample(m, pv ? t + (rnd.NextDouble() - 0.5) * 2 * jitter : null, o));
        }
        return list;
    }

    [Fact]
    public void Oscillations_CountedFromPvCrossings_AfterReachingSp()
    {
        // 样稿 tune=run：RV5 从 28 °C 升到 150 °C（15.25 min），周期 8.2 min；26 min 时完成 1 次 → 「第 2/3 次振荡」
        var c = OscillationCounter.Count(Curve(26), 150);
        Assert.True(c.Reached);
        Assert.InRange(c.ReachedAt!.Value, 15, 15.6);
        Assert.Equal((1, 2), (c.Completed, c.Current));
        Assert.Equal((false, 0, 1), (OscillationCounter.Count(Curve(10), 150).Reached, OscillationCounter.Count(Curve(10), 150).Completed, OscillationCounter.Count(Curve(10), 150).Current));
        Assert.Equal(0, OscillationCounter.Count(Curve(15.25 + 7.5), 150).Completed);   // 还差一点才回到上面
        Assert.Equal(2, OscillationCounter.Count(Curve(15.25 + 2 * 8.2 + 0.5), 150).Completed);
        // 演示的整定在第 3 次完成时结束；之后封顶 3
        double done = PidDemoCurve.DoneAt(150, 28, 5);
        Assert.Equal(3, OscillationCounter.Count(Curve(done), 150).Completed);
        Assert.Equal((3, 3), (OscillationCounter.Count(Curve(80), 150).Completed, OscillationCounter.Count(Curve(80), 150).Current));
        Assert.Equal(2, OscillationCounter.Count(Curve(done - 1), 150).Completed);
    }

    [Fact]
    public void Oscillations_IgnoreSensorNoise_AndGaps()
    {
        // ±0.15 °C 的读数噪声不会多数（回差 0.2）
        Assert.Equal(1, OscillationCounter.Count(Curve(26, jitter: 0.15), 150).Completed);
        Assert.Equal(3, OscillationCounter.Count(Curve(60, jitter: 0.15), 150).Completed);
        // 断开（null）跳过
        var s = Curve(26).Select((x, i) => i % 7 == 3 ? x with { Pv = null } : x).ToList();
        Assert.Equal(1, OscillationCounter.Count(s, 150).Completed);
        // 一直在 SP 下面：没到达
        var flat = Enumerable.Range(0, 100).Select(i => new TuneSample(i * 0.2, 120, 100)).ToList();
        Assert.False(OscillationCounter.Count(flat, 150).Reached);
    }

    [Fact]
    public void Oscillations_FallBackToOutputToggling_WithoutPv()
    {
        // 第一个关断沿 = 到达；之后每个关断沿 = 又完成一次（样稿输出：到达时关，1/4 周期后开，3/4 周期后关）
        var c = OscillationCounter.Count(Curve(26, pv: false), 150);
        Assert.True(c.Reached);
        Assert.Equal(1, c.Completed);
        Assert.Equal(3, OscillationCounter.Count(Curve(80, pv: false), 150).Completed);
        Assert.False(OscillationCounter.Count(Curve(10, pv: false), 150).Reached);
    }

    // ———————————————————— 对比文字 ————————————————————

    [Fact]
    public void Diff_Texts()
    {
        var old = new PidValues(15.1, 218, 33);
        var now = new PidValues(14.0, 182, 28);
        Assert.Equal("P 15.1 → 14.0 · I 218 → 182 s · D 33 → 28 s · 已存入中温档", PidTexts.Diff(old, now, "中温档"));
        var parts = PidTexts.DiffParts(old, now);
        Assert.Equal(new[] { "P", "I", "D" }, parts.Select(p => p.Key));
        Assert.Equal(("15.1", "14.0", ""), (parts[0].Old, parts[0].New, parts[0].Unit));
        Assert.Equal(("218", "182", " s"), (parts[1].Old, parts[1].New, parts[1].Unit));
        Assert.Equal("P 62.5 → 60.0 · I 118 → 110.5 s · D 21.4 → 20 s · 已存入高温档",
            PidTexts.Diff(new PidValues(62.5, 118, 21.4), new PidValues(60, 110.5, 20), "高温档"));
        Assert.Equal("新 PID 已写入仪表并存入中温档；以后这一档温度区间（100–200 °C）里加热都用它。整定后通道已停止加热，正在自然降温。",
            PidTexts.OkNote("中温档", 100, 200));
        Assert.Equal("RV1、RV5", PidTexts.Rvs(new[] { 1, 5 }));
    }

    // ———————————————————— 演示数据 ————————————————————

    private static DemoFleet Fleet(bool freeze = true) => new(DemoParams.Parse("fusion=1" + (freeze ? "&freeze=1" : "")));

    [Fact]
    public void Demo_PidValues_LikeTheMock()
    {
        var f = Fleet();
        var d = f.Device(1)!;
        var p = f.PidOf(d);
        Assert.Equal(new[] { "低温档", "中温档", "高温档" }, p.Bands.Select(b => b.Name));
        Assert.Equal(8, p.Channels.Length);
        PidValues V(int n, int b) { var x = p.Channels[n - 1].Bands[b]; return new PidValues(x.P, x.I, x.D); }
        Assert.Equal(new PidValues(13.0, 190, 32), V(1, 1));
        Assert.Equal(new PidValues(15.1, 218, 33), V(5, 1));
        Assert.Equal(new PidValues(15.8, 199, 36), V(8, 1));
        Assert.Equal(150, p.Channels[4].Bands[1].TunedSpC);
        Assert.Equal("11-10", p.Channels[4].Bands[1].TunedAt!.Value.ToLocalTime().ToString("MM-dd"));
        Assert.Null(p.Channels[2].Bands[0].TunedAt);                             // 低温档 RV3 没整定过
        Assert.Equal(DemoFleet.DemoFactoryPid, V(3, 0));
        Assert.All(p.Channels, c => Assert.Null(c.Bands[2].TunedAt));           // 高温档都没整定过
        Assert.All(p.Channels, c => Assert.False(c.Autotuning));
    }

    [Fact]
    public void Demo_Autotune_Guards_Then_RunsThreeOscillations_AndStoresNewPid()
    {
        var f = Fleet(freeze: false);
        var d = f.Device(1)!;
        Assert.Equal("RV1 正在运行 · 不能整定", f.StartAutotune(d, 1, 150, 1));
        Assert.Equal("RV7 已停用", f.StartAutotune(d, 7, 150, 1));
        Assert.Equal("整定温度不在本档区间内（100 – 200 °C）", f.StartAutotune(d, 5, 80, 1));
        Assert.Equal("先申请控制权", f.StartAutotune(f.Device(2)!, 4, 150, 1));
        Assert.Equal("", f.StartAutotune(d, 5, 150, 1));
        Assert.Equal("RV5 已在整定", f.StartAutotune(d, 5, 150, 1));
        var p = f.PidOf(d).Channels[4];
        Assert.Equal((true, (int?)1, (double?)150), (p.Autotuning, p.TuneBand, p.TuneSpC));
        // 升温 8 °C/min：10 min 后约 25.6 + 80
        for (int i = 0; i < 600; i++) f.Tick();
        Assert.InRange(d.Channel(5)!.TempC, 100, 110);
        Assert.Equal(100, d.Channel(5)!.HeaterPct);
        double doneAt = PidDemoCurve.DoneAt(150, 25.6, 5);
        while (f.Autotuning(d, 5) && f.TickCount < 4000) f.Tick();
        Assert.False(f.Autotuning(d, 5));
        Assert.InRange(f.TickCount / 60.0, doneAt - 0.1, doneAt + 0.1);
        // 历史里的曲线数出来正好 3 次
        var start = f.Now.AddMinutes(-doneAt);
        var samples = d.Channel(5)!.History.Where(s => s.T >= start).Select(s => new TuneSample((s.T - start).TotalMinutes, s.TempC, s.HeaterPct)).ToList();
        Assert.Equal(3, OscillationCounter.Count(samples, 150).Completed);
        var b = f.PidOf(d).Channels[4].Bands[1];
        Assert.Equal((14.0, 182d, 28d), (b.P, b.I, b.D));                           // 样稿：P × 0.93、I × 0.835、D × 0.85
        Assert.Equal(150, b.TunedSpC);
        Assert.Equal(f.Now.ToUniversalTime(), b.TunedAt);
        Assert.False(d.Channel(5)!.Heating);
    }

    [Fact]
    public void Demo_CancelAndEstop_LeavePidUnchanged()
    {
        var f = Fleet(freeze: false);
        var d = f.Device(1)!;
        Assert.Equal("", f.StartAutotune(d, 5, 150, 1));
        for (int i = 0; i < 120; i++) f.Tick();
        Assert.Equal("", f.CancelAutotune(d, 5));
        Assert.Equal("RV5 没有在整定", f.CancelAutotune(d, 5));
        Assert.False(d.Channel(5)!.Heating);
        var b = f.PidOf(d).Channels[4].Bands[1];
        Assert.Equal((15.1, 218d, 33d), (b.P, b.I, b.D));
        Assert.Equal("11-10", b.TunedAt!.Value.ToLocalTime().ToString("MM-dd"));
        // 手改：记手改时刻，整定来历保留
        Assert.Equal("", f.SetPid(d, 5, 1, 14, 200, 30));
        b = f.PidOf(d).Channels[4].Bands[1];
        Assert.Equal((14d, 200d, 30d, 150d), (b.P, b.I, b.D, b.TunedSpC!.Value));
        Assert.NotNull(b.ManualEditedAt);
        Assert.Equal("P 须大于 0，I、D 不能小于 0", f.SetPid(d, 5, 1, 0, 200, 30));
    }

    [Fact]
    public void Demo_SeedAndFinish_ForScreenshots()
    {
        var f = Fleet();
        var d = f.Device(1)!;
        f.SeedAutotune(d, 5, 150, 1, 26);
        Assert.True(f.Autotuning(d, 5));
        Assert.Equal(f.Now.AddMinutes(-26), f.AutotuneStartedAt(d, 5));
        var start = f.AutotuneStartedAt(d, 5)!.Value;
        var samples = d.Channel(5)!.History.Where(s => s.T >= start).Select(s => new TuneSample((s.T - start).TotalMinutes, s.TempC, s.HeaterPct)).ToList();
        Assert.True(samples.Count > 150);
        Assert.Equal(1, OscillationCounter.Count(samples, 150).Completed);       // 样稿「第 2/3 次振荡」
        f.FinishAutotune(d, 5);
        Assert.False(f.Autotuning(d, 5));
        var b = f.PidOf(d).Channels[4].Bands[1];
        Assert.Equal((14.0, 182d, 28d), (b.P, b.I, b.D));
        Assert.Equal("11-15", b.TunedAt!.Value.ToLocalTime().ToString("MM-dd"));
    }
}
