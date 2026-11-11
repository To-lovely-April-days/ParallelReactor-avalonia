using System;
using System.Collections.Generic;
using System.Globalization;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Core.Chart;

/// <summary>
/// 一组纵轴刻度（样稿 niceTicks 的返回值）：范围 [<see cref="Lo"/>, <see cref="Hi"/>]（刻度对齐后的）、步长、刻度值（JS <c>+v.toFixed(10)</c>）、默认小数位。
/// </summary>
public sealed record TickSet(double Lo, double Hi, double Step, IReadOnlyList<double> Ticks, int Dec);

/// <summary>
/// 纵轴刻度（样稿 js/ui.js niceTicks / fitTicks / MIN_SPAN 的移植，运算顺序一一对应，结果与 JS 逐值相同 —— ChartTicksTests 用样稿的函数算出的期望值核对）。
/// </summary>
public static class ChartTicks
{
    /// <summary>按单位的最小跨度（样稿 MIN_SPAN）：℃ 5、MPa 0.05、psi 5、% 10、rpm 10、mmol 0.2；其它单位 0（不限）。</summary>
    public static double MinSpanOf(string? unit) => unit switch
    {
        "℃" => 5,
        "MPa" => 0.05,
        "psi" => 5,
        "%" => 10,
        "rpm" => 10,
        "mmol" => 0.2,
        _ => 0,
    };

    /// <summary>
    /// 「好看」的刻度（样稿 niceTicks）：大约 <paramref name="count"/> 格，步长取 1 / 2 / 5 / 10 × 10^k；范围向外对齐到步长。
    /// 范围无效时按 [0, 1]；跨度 &lt; 1e-9 时上下各放 |hi| × 5 %（hi 为 0 时放 1）。
    /// </summary>
    public static TickSet Nice(double lo, double hi, double count)
    {
        if (!double.IsFinite(lo) || !double.IsFinite(hi)) { lo = 0; hi = 1; }
        if (hi - lo < 1e-9)
        {
            double pad = Math.Abs(hi) * 0.05;
            if (pad == 0 || double.IsNaN(pad)) pad = 1;
            lo -= pad;
            hi += pad;
        }
        double raw = (hi - lo) / Math.Max(1, count);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double e = raw / mag;
        double step = (e >= 7.5 ? 10 : e >= 3.5 ? 5 : e >= 1.5 ? 2 : 1) * mag;
        double a = Math.Floor(lo / step + 1e-9) * step, b = Math.Ceiling(hi / step - 1e-9) * step;
        var ticks = new List<double>();
        for (double v = a; v <= b + step / 2; v += step) ticks.Add(Fixed10(v));
        // 范围颠倒（lo > hi）时样稿算出 NaN（toFixed(NaN) 按 0 位）：这里直接给 0
        double dec = Math.Max(0, -Math.Floor(Math.Log10(step) + 1e-9));
        return new TickSet(a, b, step, ticks.AsReadOnly(), double.IsNaN(dec) ? 0 : (int)dec);
    }

    /// <summary>
    /// 按绘图区高度减少刻度（样稿 fitTicks）：先按 <paramref name="count"/> 格，相邻刻度间距 &lt; <paramref name="minPx"/>（默认 30 px）时逐格减少，最少 1 格。
    /// </summary>
    public static TickSet Fit(double lo, double hi, double ph, int count, double minPx = 30)
    {
        double gap = minPx != 0 && !double.IsNaN(minPx) ? minPx : 30;
        var t = Nice(lo, hi, count);
        for (int n = count - 1; n >= 1 && ph / Math.Max(1, t.Ticks.Count - 1) < gap; n--) t = Nice(lo, hi, n);
        return t;
    }

    /// <summary>JS 的 <c>+v.toFixed(10)</c>。</summary>
    private static double Fixed10(double v) => double.Parse(JsNumber.ToFixed(v, 10), NumberStyles.Float, CultureInfo.InvariantCulture);
}
