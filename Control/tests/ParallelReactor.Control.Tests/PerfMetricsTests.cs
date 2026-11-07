using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.PerfTest.Measure;
using ParallelReactor.PerfTest.Report;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>测量公式在合成数据上的正确性。</summary>
public class PerfMetricsTests
{
    /// <summary>一阶响应 + 过冲：T(t) = T0 + (SP−T0)(1−e^{−t/τ}) 再叠加一个 2 ℃ 的峰。</summary>
    private static List<Pt> FirstOrder(double t0, double sp, double tau, int seconds, double overshoot = 0, int peakAt = -1)
    {
        var pts = new List<Pt>();
        for (int s = 0; s <= seconds; s++)
        {
            double y = t0 + (sp - t0) * (1 - Math.Exp(-s / tau));
            if (peakAt >= 0) y += overshoot * Math.Exp(-Math.Pow((s - peakAt) / 20.0, 2));
            pts.Add(new Pt(s, y));
        }
        return pts;
    }

    [Fact]
    public void Describe_MeanSigmaMinMax()
    {
        var s = Metrics.Describe(new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 });
        Assert.Equal(8, s.N);
        Assert.Equal(5, s.Mean, 9);
        Assert.Equal(2.138, s.Sigma, 3);
        Assert.Equal(2, s.Min); Assert.Equal(9, s.Max);
        Assert.Equal(3.5, s.Fluctuation);
        Assert.Equal(0, Metrics.Describe(Array.Empty<double>()).N);
    }

    [Fact]
    public void Fit_RecoversSlopeAndIntercept()
    {
        var pts = Enumerable.Range(0, 100).Select(i => new Pt(i, 3 + 0.5 * i)).ToList();
        var f = Metrics.Fit(pts);
        Assert.Equal(0.5, f.Slope, 9); Assert.Equal(3, f.Intercept, 9); Assert.Equal(1, f.R2, 9);
        Assert.Equal(0.5 * 3600, Metrics.FitPerHour(pts).Slope, 6);
        Assert.False(Metrics.Fit(new List<Pt> { new(0, 1) }).Valid);
    }

    [Fact]
    public void PressureDecay_PsiPerHour_Negative_ForLeak()
    {
        // 225 psi 起每秒掉 0.001 psi → −3.6 psi/h
        var pts = Enumerable.Range(0, 1800).Select(i => new Pt(i, 225 - 0.001 * i)).ToList();
        var (psiH, kpaH, r2) = Metrics.PressureDecay(pts);
        Assert.Equal(-3.6, psiH, 6);
        Assert.Equal(-3.6 * 6.894757, kpaH, 4);
        Assert.Equal(1, r2, 9);
    }

    [Fact]
    public void Overshoot_TimeToBand_Settling_OnFirstOrderWithPeak()
    {
        var pts = FirstOrder(25, 60, 120, 1800, overshoot: 2.0, peakAt: 500);
        Assert.InRange(Metrics.Overshoot(pts, 60), 1.3, 2.0); // 峰在 t=500：一阶项尚差 0.54 ℃，叠加 +2 ℃ 峰 ≈ +1.46
        double tr = Metrics.TimeToBand(pts, 60, 0.5);
        Assert.InRange(tr, 400, 600);
        double ts05 = Metrics.SettlingTime(pts, 60, 0.5, 600);
        Assert.False(double.IsNaN(ts05));
        Assert.True(ts05 >= tr);
        Assert.True(double.IsNaN(Metrics.SettlingTime(pts, 60, 0.001, 600)));
        Assert.True(double.IsNaN(Metrics.TimeToBand(pts, 200, 0.5)));
    }

    [Fact]
    public void HeatingRate_10to90_LinearRamp()
    {
        // 25 → 85 ℃ 以 2 ℃/min 线性，SP 85：10–90% 区间斜率应为 2 ℃/min
        var pts = Enumerable.Range(0, 1801).Select(i => new Pt(i, 25 + 2.0 * i / 60.0)).ToList();
        Assert.Equal(2.0, Metrics.HeatingRate(pts, 85), 6);
        Assert.True(double.IsNaN(Metrics.HeatingRate(pts, 20)));
    }

    [Fact]
    public void IsStable_BandAndSlope()
    {
        var flat = Enumerable.Range(0, 600).Select(i => new Pt(i, 60 + 0.1 * Math.Sin(i / 10.0))).ToList();
        Assert.True(Metrics.IsStable(flat, 60, 0.5, 0.01));
        var drifting = Enumerable.Range(0, 600).Select(i => new Pt(i, 59.6 + 0.0008 * i)).ToList(); // 0.048 ℃/min
        Assert.True(Metrics.IsStable(drifting, 60, 0.5, null));
        Assert.False(Metrics.IsStable(drifting, 60, 0.5, 0.01));
        Assert.False(Metrics.IsStable(flat, 61, 0.5, null));
        Assert.False(Metrics.IsStable(new List<Pt> { new(0, 60) }, 60, 0.5, null));
    }

    [Fact]
    public void RpmError_Range_CrossChannel()
    {
        Assert.Equal(1.0, Metrics.RpmErrorPct(new[] { 303.0, 303, 303 }, 300), 9);
        Assert.Equal(-2.0, Metrics.RpmErrorPct(new[] { 98.0 }, 100), 9);
        Assert.True(double.IsNaN(Metrics.RpmErrorPct(new[] { 1.0 }, 0)));
        Assert.Equal(1.5, Metrics.Range(new[] { 60.1, 61.6, 60.8, double.NaN }), 9);
        var table = Metrics.CrossChannelTable(new Dictionary<int, IReadOnlyList<double>> { [1] = new[] { 60.0, 60.2 }, [2] = new[] { 60.5, 60.7 }, [4] = new[] { 59.9, 60.1 } });
        Assert.Equal(4, table.Count);
        Assert.Equal("RV1", table[0].Row); Assert.Equal("极差", table[3].Row);
        Assert.Equal(0.6, table[3].Mean, 9);
    }

    [Fact]
    public void Baseline_MeanPlus3Sigma_RoundedToStep()
    {
        var bl = Metrics.DeriveBaseline(new[] { 1.1, 1.3, 1.2, 1.0, 1.4, 1.2 }, "lowerBetter");
        Assert.Equal(6, bl.N);
        Assert.Equal(1.2, bl.Mean, 9);
        Assert.Equal(1.4, bl.Worst);
        // n=6（独立）：对数 → exp(0.1765 + 3.708·0.122) ≈ 1.88，与最差×1.2=1.68 取大 → 向上取档 2
        Assert.Equal(2.0, bl.SpecHigh!.Value, 9);
        Assert.StartsWith("≤ 2", bl.SpecText);
        Assert.Contains("k₁=3.71", bl.SpecText);
        // §3-3：独立 n < 6 不给规格草案，只报 x̄ / s / 最差
        var two = Metrics.DeriveBaseline(new[] { -0.3, 0.2, 0.1, -0.1 }, "twoSided");
        Assert.Equal(0.3, two.Worst, 9);
        Assert.Null(two.SpecHigh);
        Assert.Contains("n=4 < 6", two.SpecText);
        var hb = Metrics.DeriveBaseline(new[] { 5.0, 6, 5.5 }, "higherBetter");
        Assert.Null(hb.SpecLow);
        // 同日 4 通道不独立：24 个值但只有 3 轮 → 仍不给规格
        var dep = Metrics.DeriveBaseline(Enumerable.Range(0, 24).Select(i => 1.0 + i * 0.01).ToList(), "lowerBetter", independentN: 3);
        Assert.Equal(24, dep.N); Assert.Null(dep.SpecHigh);
        // n=8 双侧：k₂=3.732
        var two8 = Metrics.DeriveBaseline(new[] { -0.3, 0.2, 0.1, -0.1, 0.05, -0.05, 0.15, -0.2 }, "twoSided");
        Assert.NotNull(two8.SpecHigh); Assert.Contains("k₂=3.73", two8.SpecText);
        Assert.Equal("—", Metrics.DeriveBaseline(Array.Empty<double>(), "lowerBetter").SpecText);
        // ISO 16269-6 k 表（95/95）：n=6 3.708/4.414，n=10 2.911/3.379，中间 n 取相邻较小 n，n<6 NaN
        Assert.Equal(3.708, Metrics.ToleranceK1(6)); Assert.Equal(4.414, Metrics.ToleranceK2(6));
        Assert.Equal(2.911, Metrics.ToleranceK1(11)); Assert.Equal(3.379, Metrics.ToleranceK2(11));
        Assert.True(double.IsNaN(Metrics.ToleranceK1(5)));
    }

    [Theory]
    [InlineData(0.07, 0.1)] [InlineData(0.1, 0.1)] [InlineData(0.11, 0.2)] [InlineData(0.3, 0.5)] [InlineData(0.6, 1)] [InlineData(1.5, 2)] [InlineData(3, 5)] [InlineData(7, 10)] [InlineData(12, 20)]
    public void RoundUpToStep(double x, double expected) => Assert.Equal(expected, Metrics.RoundUpToStep(x), 9);

    [Fact]
    public void Fmt_UsesMinusSignAndDash()
    {
        Assert.Equal("—", Metrics.Fmt(double.NaN));
        Assert.Equal("−1.5", Metrics.Fmt(-1.5));
        Assert.Equal("225.3", Metrics.Fmt(225.34));
    }

    [Fact]
    public void SvgChart_RendersSeriesBandsMarkers()
    {
        var temp = FirstOrder(25, 60, 100, 600);
        var press = Enumerable.Range(0, 601).Select(i => new Pt(i, 225)).ToList();
        var svg = SvgChart.Render(new[] { new SvgSeries("RV1 温度", SvgChart.LineTemp, temp, false), new SvgSeries("压力", SvgChart.LinePress, press, true) },
            null, null, "测试时间 (mins)", "温度 (℃)", "压力 (psi)", new[] { new SvgBand(100, 400, "保温", "#dcf8c7") }, new[] { new SvgMarker(50, "加热") });
        Assert.StartsWith("<svg", svg);
        Assert.Contains("viewBox=\"0 0 470 190\"", svg);
        Assert.Equal(2, CountOf(svg, "<polyline"));
        Assert.Contains("保温", svg);
        Assert.Contains("stroke-dasharray", svg);
        Assert.Contains("温度 (℃)", svg);
        Assert.DoesNotContain("NaN", svg);
    }

    private static int CountOf(string s, string sub) { int n = 0, i = 0; while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; } return n; }
}
