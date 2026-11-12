using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 c2-ui-3：零下的温度。样稿 chartBands 无条件把温度轴的下限夹到 0 —— 全部低于约 −3.6 ℃ 时下限 0 高过上限，坐标轴成了 NaN（没有刻度、曲线不见），
/// −2 ℃ 时曲线画在绘图区下面。真机的读数（int16 / 10^dpt）没有故障过滤，欠量程、故障值、零下环境都会给出负温度。
/// 现在只在数据都 ≥ 0 时夹到 0（这时与样稿逐值相同：ChartTicksTests.Bands_MatchMock 的向量照旧通过，这里再按样稿的公式扫一遍），
/// 有负读数时分带照样成立：温度在上半区、刻度有效、每个点都在绘图区里。
/// </summary>
public class M5ReviewChartTests
{
    private static readonly ModelProfile Pr8 = ModelProfiles.Embedded.Get("PR8")!;
    private static readonly DateTime Now = new(2026, 11, 8, 2, 12, 0, DateTimeKind.Utc);

    /// <summary>近 30 min、10 s 一个点的温度（压力平稳 0.35 MPa），经 ReactorChartOptions.Build（分带）。</summary>
    private static ChartOptions Build(Func<int, double> temp)
    {
        double now = ChartTime.Ms(Now);
        ChartPoint[] S(Func<int, double> f) => Enumerable.Range(0, 181).Select(i => new ChartPoint(now - (180 - i) * 10000.0, f(i))).ToArray();
        return ReactorChartOptions.Build(Pr8, 1, Now, 30, S(temp), Array.Empty<ChartPoint>(), S(_ => 0.35), Array.Empty<ChartPoint>(),
            null, 0.35, alarm: false);
    }

    /// <summary>样稿 ui.chartBands 的温度轴（js/ui.js，无条件夹到 0）。</summary>
    private static (double Min, double Max) MockBand(double lo, double hi)
    {
        double span = Math.Max(hi - lo, 20);
        if (hi - lo <= 24)
        {
            double max = hi + span * 0.18;
            return (Math.Max(0, Math.Max(lo - span * 1.2, max - span * 2.3)), max);
        }
        return (Math.Max(0, lo - span * 0.08), hi + span * 0.08);
    }

    public static IEnumerable<object[]> SubZero() => new[] { -2.0, -5.0, -40.0, -3276.8 }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(SubZero))]
    public void SubZeroTemperature_BandAxisAndCurveStayValid(double t)
    {
        // 样稿的分带：−2 ℃ 时下限 0 在读数之上（曲线在绘图区下面），更低时下限高过上限（坐标轴 NaN）
        var mock = MockBand(t, t);
        Assert.True(mock.Min > t, $"样稿的下限 {mock.Min} 应在读数 {t} 之上（缺陷本身）");

        // 平稳的读数（±0.3 ℃ 噪声）与恒定的读数
        foreach (var (name, f) in new (string, Func<int, double>)[] { ("恒定", _ => t), ("噪声 ±0.3", i => t + 0.3 * Math.Sin(i * 0.7)) })
        {
            var opts = Build(f);
            var a = opts.YAxes[0];
            double lo = opts.Series[0].Points.Min(p => p.V), hi = opts.Series[0].Points.Max(p => p.V);
            Assert.True(a.Min is double mn && a.Max is double mx && double.IsFinite(mn) && double.IsFinite(mx) && mn < mx, $"{t} ℃ {name}：分带 [{a.Min}, {a.Max}]");
            Assert.True(a.Min <= lo && a.Max >= hi, $"{t} ℃ {name}：读数 [{lo}, {hi}] 不在分带 [{a.Min}, {a.Max}] 里");
            Assert.Equal(5, a.MaxTicks);
            AssertDrawn(opts, $"{t} ℃ {name}", upperHalf: true);
        }
    }

    [Fact]
    public void SubZeroAndCrossingZero_VaryingData_FillThePlot()
    {
        // 变化大（跨度 > 24）：按数据铺满，上下各留 8 %，不夹到 0
        foreach (var (name, f) in new (string, Func<int, double>)[]
                 {
                     ("−60 → −20", i => -60 + 40 * i / 180.0),
                     ("−10 → 30（过零）", i => -10 + 40 * i / 180.0),
                     ("−3 → 2（平稳、过零）", i => -3 + 5 * i / 180.0),
                     ("故障值 −3276.8 与 25 交替", i => i % 30 == 7 ? -3276.8 : 25),
                 })
        {
            var opts = Build(f);
            var a = opts.YAxes[0];
            double lo = opts.Series[0].Points.Min(p => p.V), hi = opts.Series[0].Points.Max(p => p.V);
            Assert.True(a.Min < lo && a.Max > hi, $"{name}：读数 [{lo}, {hi}] 不在分带 [{a.Min}, {a.Max}] 里");
            AssertDrawn(opts, name, upperHalf: false);
        }
    }

    /// <summary>数据都 ≥ 0 时与样稿的公式逐位相同（含正好 0 的下限、接近 0 的平稳读数）。</summary>
    [Fact]
    public void NonNegativeData_SameAsMock()
    {
        var rnd = new Random(20261111);
        var sets = new List<double[]> { new[] { 0.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, 3.5 }, new[] { 0.2, 0.8 }, new[] { 2.0, 2.0 }, new[] { 25.0 }, new[] { 0.0, 120.0 } };
        for (int k = 0; k < 400; k++)
        {
            double b = k % 4 == 0 ? 0 : rnd.NextDouble() * Math.Pow(10, rnd.Next(-2, 4));
            double amp = rnd.NextDouble() * Math.Pow(10, rnd.Next(-2, 3));
            sets.Add(Enumerable.Range(0, 1 + rnd.Next(40)).Select(_ => b + rnd.NextDouble() * amp).ToArray());
        }
        foreach (var v in sets)
        {
            var opts = new ChartOptions
            {
                Series = new[] { new ChartSeries("t", v.Select((x, i) => new ChartPoint(i, x)).ToArray()) },
                YAxes = new[] { new ChartAxis { Label = "T" } },
            };
            var a = ChartBands.Apply(opts).YAxes[0];
            var m = MockBand(v.Min(), v.Max());
            Assert.True(a.Min.Equals(m.Min) && a.Max.Equals(m.Max), $"[{string.Join(", ", v.Take(6))}…]：{a.Min}/{a.Max} ≠ 样稿 {m.Min}/{m.Max}");
        }
    }

    /// <summary>版面：刻度有效、每个温度点（与末端圆点）都在绘图区里；平稳时温度在上半区（分带的本意）。</summary>
    private static void AssertDrawn(ChartOptions opts, string what, bool upperHalf)
    {
        var g = ChartLayout.Compute(opts, 640, 315, ChartLayout.EstimateWidth);
        Assert.True(g.Drawn, what);
        var ax = g.Axes[0];
        Assert.True(double.IsFinite(ax.Lo) && double.IsFinite(ax.Hi) && double.IsFinite(ax.Step) && ax.Lo < ax.Hi && ax.Step > 0,
            $"{what}：温度轴 Lo={ax.Lo} Hi={ax.Hi} Step={ax.Step}");
        Assert.True(g.LeftTicks.Count >= 2 && g.LeftTicks.All(k => double.IsFinite(k.Y) && k.Y >= g.PadT - 0.01 && k.Y <= g.PadT + g.Ph + 0.01),
            $"{what}：左轴刻度 {string.Join(" ", g.LeftTicks.Select(k => k.Text + "@" + k.Y.ToString("0.#")))}");
        int ti = g.Series.ToList().FindIndex(s => s.Axis == 0 && s.Name == ReactorChartOptions.TempName);
        Assert.True(ti >= 0, what);
        double[] xs = new double[8], ys = new double[8];
        int n = g.ScreenPoints(ti, ref xs, ref ys);
        Assert.Equal(opts.Series[0].Points.Count, n);
        for (int i = 0; i < n; i++)
            Assert.True(double.IsFinite(ys[i]) && ys[i] >= g.PadT - 0.01 && ys[i] <= g.PadT + g.Ph + 0.01, $"{what}：第 {i} 个点 y = {ys[i]}（绘图区 {g.PadT}–{g.PadT + g.Ph}）");
        var end = g.EndDot(ti);
        Assert.True(end is { } e && double.IsFinite(e.Y) && e.Y >= g.PadT && e.Y <= g.PadT + g.Ph, $"{what}：末端圆点 {end}");
        if (upperHalf) Assert.True(ys.Take(n).All(y => y < g.PadT + g.Ph / 2), $"{what}：平稳的温度应在上半区");
    }
}
