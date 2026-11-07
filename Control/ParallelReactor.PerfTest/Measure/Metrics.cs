using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.PerfTest.Measure;

/// <summary>一个时间点的采样（秒为相对时间）。</summary>
public readonly record struct Pt(double Sec, double Y);

/// <summary>最小二乘直线 y = Intercept + Slope·x（思路同 HMI 的 LeakFit，独立实现不引用 HMI 工程）。</summary>
public readonly record struct LineFit(double Slope, double Intercept, double R2, int N)
{
    public bool Valid => !double.IsNaN(Slope);
}

/// <summary>基本统计。</summary>
public readonly record struct Stats(int N, double Mean, double Sigma, double Min, double Max)
{
    public double PeakToPeak => Max - Min;
    /// <summary>波动度 ±(max−min)/2。</summary>
    public double Fluctuation => (Max - Min) / 2;
    public static readonly Stats Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN);
}

/// <summary>§3 基线行：x̄、s、最差值、x̄±3σ 建议规格（取整到档）。</summary>
public sealed record Baseline(int N, double Mean, double Sigma, double Worst, double? SpecLow, double? SpecHigh, string Direction, string SpecText);

/// <summary>测量公式（纯函数，全部可单测）。时间单位：秒；斜率输出单位按函数名。</summary>
public static class Metrics
{
    public const double KpaPerPsi = 6.894757;
    public const double MPaPerPsi = 0.00689476;

    public static Stats Describe(IReadOnlyList<double> ys)
    {
        var v = ys.Where(y => !double.IsNaN(y)).ToArray();
        if (v.Length == 0) return Stats.Empty;
        double mean = v.Average();
        double ss = 0; foreach (var y in v) ss += (y - mean) * (y - mean);
        double sigma = v.Length > 1 ? Math.Sqrt(ss / (v.Length - 1)) : 0;
        return new Stats(v.Length, mean, sigma, v.Min(), v.Max());
    }

    public static LineFit Fit(IReadOnlyList<Pt> pts)
    {
        int n = pts.Count;
        if (n < 2) return new LineFit(double.NaN, double.NaN, double.NaN, n);
        double mx = 0, my = 0;
        foreach (var p in pts) { mx += p.Sec; my += p.Y; }
        mx /= n; my /= n;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in pts) { double dx = p.Sec - mx, dy = p.Y - my; sxx += dx * dx; sxy += dx * dy; syy += dy * dy; }
        if (sxx < 1e-12) return new LineFit(double.NaN, double.NaN, double.NaN, n);
        double slope = sxy / sxx;
        double r2 = syy < 1e-24 ? 1.0 : sxy * sxy / (sxx * syy);
        return new LineFit(slope, my - slope * mx, r2, n);
    }

    /// <summary>斜率换算成每小时（输入 Sec）。</summary>
    public static LineFit FitPerHour(IReadOnlyList<Pt> pts) { var f = Fit(pts); return f.Valid ? f with { Slope = f.Slope * 3600 } : f; }
    /// <summary>斜率换算成每分钟（输入 Sec）。</summary>
    public static LineFit FitPerMinute(IReadOnlyList<Pt> pts) { var f = Fit(pts); return f.Valid ? f with { Slope = f.Slope * 60 } : f; }

    /// <summary>压力衰减 psi/h（负 = 泄漏）；另给 kPa/h。</summary>
    public static (double PsiPerHour, double KpaPerHour, double R2) PressureDecay(IReadOnlyList<Pt> psi)
    {
        var f = FitPerHour(psi);
        return (f.Slope, f.Slope * KpaPerPsi, f.R2);
    }

    /// <summary>超调 σ = T_peak − SP（无样本 → NaN）。</summary>
    public static double Overshoot(IReadOnlyList<Pt> pv, double sp)
        => pv.Count == 0 ? double.NaN : pv.Max(p => p.Y) - sp;

    /// <summary>首次进入 [sp−band, +∞)（t_r：首次达到 T_s − 0.5 ℃）的时刻（秒，相对序列起点）；未达到 → NaN。</summary>
    public static double TimeToBand(IReadOnlyList<Pt> pv, double sp, double band)
    {
        if (pv.Count == 0) return double.NaN;
        double t0 = pv[0].Sec;
        foreach (var p in pv) if (p.Y >= sp - band) return p.Sec - t0;
        return double.NaN;
    }

    /// <summary>首次达到 ≥ value（如压力目标 − max(1 psi, 3%)）。</summary>
    public static double TimeToValue(IReadOnlyList<Pt> y, double value)
    {
        if (y.Count == 0) return double.NaN;
        double t0 = y[0].Sec;
        foreach (var p in y) if (p.Y >= value) return p.Sec - t0;
        return double.NaN;
    }

    /// <summary>
    /// 稳定时间 t_s(b)：进入 ±b 带内且之后 ≥ holdSec（默认 10 min）不再越出的时刻（秒）。
    /// 带内段（含序列末尾那一段）的持续时间必须 ≥ holdSec 才算稳定（数据不足 holdSec 时为 NaN，与方案「之后 ≥ 10 min 不越出」一致）。
    /// </summary>
    public static double SettlingTime(IReadOnlyList<Pt> pv, double sp, double band, double holdSec = 600)
    {
        if (pv.Count == 0) return double.NaN;
        double t0 = pv[0].Sec;
        int i = 0;
        while (i < pv.Count)
        {
            if (Math.Abs(pv[i].Y - sp) > band) { i++; continue; }
            int j = i;
            while (j + 1 < pv.Count && Math.Abs(pv[j + 1].Y - sp) <= band) j++;
            if (pv[j].Sec - pv[i].Sec >= holdSec) return pv[i].Sec - t0;
            i = j + 1;
        }
        return double.NaN;
    }

    /// <summary>升温速率 ℃/min：span = (sp−delta) − T₀，在 [T₀+0.1·span, T₀+0.9·span] 内做最小二乘斜率（§2 T1）。</summary>
    public static double HeatingRate(IReadOnlyList<Pt> pv, double sp, double delta = 0)
    {
        if (pv.Count < 3) return double.NaN;
        double t0v = pv[0].Y;
        double span = (sp - delta) - t0v;
        if (span <= 0) return double.NaN;
        double lo = t0v + 0.1 * span, hi = t0v + 0.9 * span;
        var seg = pv.Where(p => p.Y >= lo && p.Y <= hi).ToList();
        if (seg.Count < 2) return double.NaN;
        return FitPerMinute(seg).Slope;
    }

    /// <summary>窗口内是否稳定：全部样本在 ref±band 内，且（给出时）|斜率| ≤ maxSlopePerMin。样本数 &lt; 2 → false。</summary>
    public static bool IsStable(IReadOnlyList<Pt> window, double? reference, double? band, double? maxSlopePerMin)
    {
        if (window.Count < 2) return false;
        if (band.HasValue && reference.HasValue)
            foreach (var p in window) if (Math.Abs(p.Y - reference.Value) > band.Value) return false;
        if (maxSlopePerMin.HasValue)
        {
            var f = FitPerMinute(window);
            if (!f.Valid || Math.Abs(f.Slope) > maxSlopePerMin.Value) return false;
        }
        return true;
    }

    /// <summary>转速误差 e_n = (n̄_meas − n_set)/n_set × 100%。</summary>
    public static double RpmErrorPct(IReadOnlyList<double> measured, double set)
    {
        if (set == 0 || measured.Count == 0) return double.NaN;
        return (measured.Average() - set) / set * 100.0;
    }

    /// <summary>跨通道极差 max−min（通道间一致性）。</summary>
    public static double Range(IReadOnlyList<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).ToArray();
        return v.Length == 0 ? double.NaN : v.Max() - v.Min();
    }

    /// <summary>
    /// 跨通道比较表：每通道一行（均值 / σ / 最小 / 最大），末行极差。
    /// </summary>
    public static IReadOnlyList<(string Row, double Mean, double Sigma, double Min, double Max)> CrossChannelTable(IReadOnlyDictionary<int, IReadOnlyList<double>> perChannel)
    {
        var rows = new List<(string, double, double, double, double)>();
        var means = new List<double>();
        foreach (var kv in perChannel.OrderBy(k => k.Key))
        {
            var s = Describe(kv.Value);
            rows.Add(($"RV{kv.Key}", s.Mean, s.Sigma, s.Min, s.Max));
            means.Add(s.Mean);
        }
        var r = Range(means);
        rows.Add(("极差", r, double.NaN, double.NaN, double.NaN));
        return rows;
    }

    /// <summary>
    /// §3 基线推导（方案 §3-2 / §3-3）：
    /// <list type="bullet">
    /// <item>独立样本数 <paramref name="independentN"/>（同日 4 通道不是独立样本：调用方传「轮次 / 运行数」），&lt; 6 只报 x̄ / s / 最差，不给规格草案；</item>
    /// <item>n ≥ 6：容差区间 95% 含量 / 95% 置信，k 取 ISO 16269-6 表（单侧 k₁、双侧 k₂，中间 n 取相邻较小 n 的 k，偏保守）；</item>
    /// <item>单侧偏态指标（lowerBetter / higherBetter 且全部 &gt; 0）先取自然对数再算，反变换回原单位；</item>
    /// <item>限值再与「最差 × 1.2 / 0.8」取保守值并向 1/2/5 × 10^k 取档。</item>
    /// </list>
    /// </summary>
    public static Baseline DeriveBaseline(IReadOnlyList<double> values, string direction, int? independentN = null)
    {
        var clean = values.Where(v => !double.IsNaN(v)).ToList();
        var st = Describe(clean);
        if (st.N == 0) return new Baseline(0, double.NaN, double.NaN, double.NaN, null, null, direction, "—");
        double sigma = st.N > 1 ? st.Sigma : 0;
        int n = independentN ?? st.N;
        double worst = direction switch { "lowerBetter" => st.Max, "higherBetter" => st.Min, "twoSided" => clean.Select(Math.Abs).Max(), _ => double.NaN };
        if (direction is not ("lowerBetter" or "higherBetter" or "twoSided")) return new Baseline(st.N, st.Mean, sigma, worst, null, null, direction, "—（摸底，不定规格）");
        if (n < MinIndependentN || st.N < 2) return new Baseline(st.N, st.Mean, sigma, worst, null, null, direction, $"—（独立 n={n} < {MinIndependentN}，只报 x̄ / s / 最差）");
        switch (direction)
        {
            case "lowerBetter":
            {
                double k = ToleranceK1(n);
                bool log = clean.All(v => v > 0);
                double lim = log ? Math.Exp(Describe(clean.Select(v => Math.Log(v)).ToList()) is var ls ? ls.Mean + k * ls.Sigma : 0) : st.Mean + k * sigma;
                double hi = RoundUpToStep(Math.Max(lim, worst * 1.2));
                return new Baseline(st.N, st.Mean, sigma, worst, null, hi, direction, $"≤ {Fmt(hi)}（n={n}，k₁={k:0.00}{(log ? "，对数" : "")}）");
            }
            case "higherBetter":
            {
                double k = ToleranceK1(n);
                bool log = clean.All(v => v > 0);
                double lim = log ? Math.Exp(Describe(clean.Select(v => Math.Log(v)).ToList()) is var ls ? ls.Mean - k * ls.Sigma : 0) : st.Mean - k * sigma;
                double lo = RoundDownToStep(Math.Min(lim, worst * 0.8));
                return new Baseline(st.N, st.Mean, sigma, worst, lo, null, direction, $"≥ {Fmt(lo)}（n={n}，k₁={k:0.00}{(log ? "，对数" : "")}）");
            }
            default:
            {
                double k = ToleranceK2(n);
                double lim = RoundUpToStep(Math.Max(Math.Abs(st.Mean) + k * sigma, worst * 1.2));
                return new Baseline(st.N, st.Mean, sigma, worst, -lim, lim, direction, $"±{Fmt(lim)}（n={n}，k₂={k:0.00}）");
            }
        }
    }

    /// <summary>§3-3：容差区间只对独立重复 ≥ 6 的指标算。</summary>
    public const int MinIndependentN = 6;

    // ISO 16269-6 容差区间 k 因子，含量 p = 0.95、置信 1−α = 0.95
    private static readonly (int N, double K1, double K2)[] ToleranceTable =
    {
        (6, 3.708, 4.414), (7, 3.399, 4.007), (8, 3.187, 3.732), (9, 3.031, 3.532), (10, 2.911, 3.379), (12, 2.736, 3.162), (15, 2.566, 2.954),
        (20, 2.396, 2.752), (25, 2.292, 2.631), (30, 2.220, 2.549), (40, 2.126, 2.445), (50, 2.065, 2.379), (100, 1.927, 2.233), (1000, 1.736, 2.036),
    };

    /// <summary>单侧 k₁（n 介于表值之间取相邻较小 n，偏保守；n &lt; 6 返回 NaN）。</summary>
    public static double ToleranceK1(int n) => LookupK(n, true);
    /// <summary>双侧 k₂。</summary>
    public static double ToleranceK2(int n) => LookupK(n, false);

    private static double LookupK(int n, bool oneSided)
    {
        if (n < MinIndependentN) return double.NaN;
        var row = ToleranceTable[0];
        foreach (var r in ToleranceTable) { if (r.N <= n) row = r; else break; }
        return oneSided ? row.K1 : row.K2;
    }

    private static readonly double[] StepMantissa = { 1, 2, 5 };

    /// <summary>向上取整到 1/2/5 × 10^k 档。</summary>
    public static double RoundUpToStep(double x)
    {
        if (x <= 0 || double.IsNaN(x) || double.IsInfinity(x)) return x <= 0 ? 0 : x;
        double exp = Math.Floor(Math.Log10(x));
        double scale = Math.Pow(10, exp);
        foreach (var m in StepMantissa) if (m * scale >= x - 1e-12 * scale) return m * scale;
        return 10 * scale;
    }

    /// <summary>向下取整到 1/2/5 × 10^k 档（≤0 → 0）。</summary>
    public static double RoundDownToStep(double x)
    {
        if (x <= 0 || double.IsNaN(x) || double.IsInfinity(x)) return 0;
        double exp = Math.Floor(Math.Log10(x));
        double scale = Math.Pow(10, exp);
        double best = scale;
        foreach (var m in StepMantissa) if (m * scale <= x + 1e-12 * scale) best = m * scale;
        return best;
    }

    public static string Fmt(double v, int digits = 3)
    {
        if (double.IsNaN(v)) return "—";
        if (double.IsInfinity(v)) return v > 0 ? "∞" : "−∞";
        double a = Math.Abs(v);
        string s = a >= 100 ? v.ToString("0.#") : a >= 10 ? v.ToString("0.##") : v.ToString("0." + new string('#', digits));
        return s.Replace("-", "−");
    }
}
