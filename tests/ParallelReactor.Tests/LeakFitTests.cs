using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>泄漏测试：最小二乘斜率 / R²、温度补偿、逐点汇总（纯逻辑）。</summary>
public class LeakFitTests
{
    [Fact]
    public void PerfectLine_SlopeInterceptAndR2()
    {
        var pts = Enumerable.Range(0, 10).Select(i => (X: (double)i, Y: 3.0 - 0.5 * i)).ToList();
        var f = LeakFit.Fit(pts);
        Assert.Equal(-0.5, f.Slope, 12);
        Assert.Equal(3.0, f.Intercept, 12);
        Assert.Equal(1.0, f.R2, 12);
        Assert.Equal(10, f.N);
    }

    [Fact]
    public void KnownNoisyData_R2()
    {
        // x = 1..5, y = 2,4,5,4,5 → 斜率 0.6，截距 2.2，R² = 0.6
        var pts = new List<(double, double)> { (1, 2), (2, 4), (3, 5), (4, 4), (5, 5) };
        var f = LeakFit.Fit(pts);
        Assert.Equal(0.6, f.Slope, 10);
        Assert.Equal(2.2, f.Intercept, 10);
        Assert.Equal(0.6, f.R2, 10);
    }

    [Fact]
    public void Degenerate_IsNaN()
    {
        Assert.False(LeakFit.Fit(new List<(double, double)>()).Valid);
        Assert.False(LeakFit.Fit(new List<(double, double)> { (1, 2) }).Valid);
        Assert.False(LeakFit.Fit(new List<(double, double)> { (1, 2), (1, 3) }).Valid);   // x 无变化
        var flat = LeakFit.Fit(new List<(double, double)> { (0, 5), (1, 5), (2, 5) });
        Assert.Equal(0, flat.Slope, 12);
        Assert.Equal(1, flat.R2, 12);
    }

    [Fact]
    public void FitPerHour_ConvertsSecondsSlope()
    {
        // 每 10 s 降 0.01 psi → −3.6 psi/h
        var pts = Enumerable.Range(0, 7).Select(i => (Sec: i * 10.0, Y: 50 - 0.001 * i * 10)).ToList();
        Assert.Equal(-3.6, LeakFit.FitPerHour(pts).Slope, 9);
    }

    [Fact]
    public void Slope_MatchesExistingDropRate_WithOppositeSign()
    {
        var rnd = new Random(1);
        var minPsi = Enumerable.Range(0, 30).Select(i => (Min: i / 6.0, Psi: 50 - 0.05 * i + rnd.NextDouble() * 0.02)).ToList();
        double drop = LeakMath.DropRatePerHour(minPsi);
        var fit = LeakFit.FitPerHour(minPsi.Select(p => (p.Min * 60, p.Psi)).ToList());
        Assert.Equal(-drop, fit.Slope, 9);
    }

    [Fact]
    public void Compensation_RemovesPureThermalDrift()
    {
        // 密闭无泄漏：P_abs ∝ T。降温 30→28℃ 时表压下降，但补偿到 T_ref=30℃ 后绝压恒定
        double pAbs0 = LeakFit.PsigToAbsKpa(50), tRef = 30;
        var rec = new LeakRecord(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.FromHours(8)));
        rec.TRef[1] = tRef;
        for (int i = 0; i <= 10; i++)
        {
            double t = tRef - 0.2 * i;
            double pAbs = pAbs0 * (t + 273.15) / (tRef + 273.15);
            double psig = (pAbs - LeakFit.AtmKpa) / LeakFit.KpaPerPsi;
            rec.Add(1, LeakRecord.Measure, rec.Start.AddSeconds(i * 10), 8000 + i, psig, t);
        }
        var s = rec.Summarize(new[] { (1, "ok", "") }, null).Single();
        Assert.True(s.SlopeRawPsiPerH < -1);                 // 未补偿：看起来在漏
        Assert.Equal(0, s.SlopeCompPsiPerH!.Value, 6);       // 补偿后：无泄漏
        Assert.Equal(s.SlopeRawPsiPerH!.Value * LeakFit.KpaPerPsi, s.SlopeRawKpaPerH!.Value, 9);
        Assert.Equal(11, s.N);
        Assert.Equal(11, s.NComp);
        Assert.Equal(0, s.WindowStartSec!.Value, 6);
        Assert.Equal(100, s.WindowEndSec!.Value, 6);
        Assert.Equal(50, s.StartPsig!.Value, 6);
        Assert.Equal(1.0, s.R2Raw!.Value, 9);
    }

    [Fact]
    public void Summary_OnlyUsesMeasureWindow_AndHandlesOfflineTemperature()
    {
        var rec = new LeakRecord(DateTimeOffset.Now);
        rec.TRef[2] = double.NaN;   // 测量起点温控离线
        rec.Add(2, LeakRecord.Fill, rec.Start.AddSeconds(1), 7000, 10, double.NaN);
        rec.Add(2, LeakRecord.Equilibrate, rec.Start.AddSeconds(5), 7100, 49, double.NaN);
        for (int i = 0; i < 4; i++)
            rec.Add(2, LeakRecord.Measure, rec.Start.AddSeconds(60 + i * 10), 7200, 50 - i * 0.01, double.NaN);
        var s = rec.Summarize(new[] { (2, "ok", "x"), (3, "skip", "不测") }, new Dictionary<int, double> { [2] = 3.6 });
        Assert.Equal(4, s[0].N);
        Assert.Equal(60, s[0].WindowStartSec!.Value, 6);
        Assert.Equal(-3.6, s[0].SlopeRawPsiPerH!.Value, 6);
        Assert.Null(s[0].SlopeCompPsiPerH);   // 无有效温度：不臆造补偿结果
        Assert.Equal(0, s[0].NComp);
        Assert.Equal(3.6, s[0].JudgedDropPsiPerH);
        Assert.Equal(0, s[1].N);
        Assert.Null(s[1].SlopeRawPsiPerH);

        var csv = rec.SamplesCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 6, csv.Length);
        Assert.Contains(",measure,", csv[^1]);
        Assert.EndsWith(",0", csv[^1].TrimEnd('\r'));   // 已温度补偿 = 0
        Assert.Contains("\"channels\"", rec.SummaryJson(s, new { testPsi = 50 }));
    }
}
