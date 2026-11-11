using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Chart;

/// <summary>
/// 双轴分带（样稿 js/ui.js ui.chartBands 的移植，运算顺序一一对应）：温度曲线在上半区、压力曲线在下半区，两条线不叠在一起。
/// 按数据给 yAxes[0] / yAxes[1] 设 Min / Max / MaxTicks = 5：
/// <list type="bullet">
/// <item>温度：跨度至少 20 ℃（±0.3 ℃ 的噪声不会被自动缩放成「地震图」）。平稳（hi − lo ≤ 24）：上方留 18 %、下方留出一个多量程（温度在上半区）；
///   变化大：按数据铺满（上下各 8 %）。下限不低于 0 —— 只在数据都 ≥ 0 时（与样稿逐值相同）；有低于 0 的读数时不夹（样稿无条件夹到 0：
///   全部低于约 −3.6 ℃ 时下限 0 高过上限，坐标轴成了 NaN、没有刻度、曲线不见；−2 ℃ 时曲线画在绘图区下面）。夹完仍然颠倒时按数据的范围。</item>
/// <item>压力：跨度至少 max(0.1, hi × 8 %, 5 × 10^−dec)。接近 0（hi &lt; 量程 × 5 %，未充压）：[0, max(hi × 1.6, 量程 × 10 %)]，贴底；
///   平稳（hi − lo ≤ max(0.1, hi × 25 %)）：压力在下半区（下方留 18 %，跨度 × 2.3）；变化大：按数据铺满。
///   给了上限值（接近 / 超过上限时）：上限 × 1.06 也要画进来。</item>
/// </list>
/// 值取各系列的<b>全部</b>有效点（不按横轴窗口筛选，同样稿）。没有温度 / 压力点时对应的轴不动。返回新的 <see cref="ChartOptions"/>（输入不变）。
/// </summary>
public static class ChartBands
{
    /// <param name="opts">曲线输入（至少一条纵轴才会改）。</param>
    /// <param name="pMax">型号的压力量程（MPa）；0 / null 按 1（JS <c>o.pMax || 1</c>）。</param>
    /// <param name="limit">要画进来的上限值（接近 / 超过上限时 = 量程）；null = 不画上限线。</param>
    public static ChartOptions Apply(ChartOptions opts, double? pMax = null, double? limit = null)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var axes = new List<ChartAxis>(opts.YAxes);
        if (axes.Count > 0 && Range(opts, 0) is (double tlo, double thi))
        {
            double span = Math.Max(thi - tlo, 20);
            // 夹到 0 只在数据都 ≥ 0 时（真机的 int16 / 10^dpt 读数没有故障过滤：传感器欠量程、故障值、零下环境都会给负温度）
            double floor = tlo >= 0 ? 0 : double.NegativeInfinity;
            double min, max;
            if (thi - tlo <= 24)
            {
                max = thi + span * 0.18;
                min = Math.Max(floor, Math.Max(tlo - span * 1.2, max - span * 2.3));
            }
            else
            {
                min = Math.Max(floor, tlo - span * 0.08);
                max = thi + span * 0.08;
            }
            // 兜底（溢出之类）：仍然颠倒时按数据的范围，坐标轴不会成 NaN
            if (!(max > min)) { min = tlo; max = thi; }
            axes[0] = axes[0] with { Min = min, Max = max, MaxTicks = 5 };
        }
        if (axes.Count > 1 && Range(opts, 1) is (double plo, double phi))
        {
            double pm = pMax is double pv && pv != 0 && !double.IsNaN(pv) ? pv : 1;
            int dec = axes[1].Dec ?? 3;
            double span = Math.Max(Math.Max(phi - plo, 0.1), Math.Max(phi * 0.08, 5 * Math.Pow(10, -dec)));
            double min, max;
            if (phi < pm * 0.05)
            {
                min = 0;
                max = Math.Max(phi * 1.6, pm * 0.1);
            }
            else if (phi - plo <= Math.Max(0.1, phi * 0.25))
            {
                min = Math.Max(0, plo - span * 0.18);
                max = min + span * 2.3;
            }
            else
            {
                min = Math.Max(0, plo - span * 0.08);
                max = phi + span * 0.08;
            }
            if (limit is double lim) max = Math.Max(max, lim * 1.06);
            axes[1] = axes[1] with { Min = min, Max = max, MaxTicks = 5 };
        }
        return opts with { YAxes = axes.AsReadOnly() };
    }

    /// <summary>第 <paramref name="axis"/> 条轴上全部系列的有效值的最小 / 最大值；没有为 null。</summary>
    private static (double Lo, double Hi)? Range(ChartOptions opts, int axis)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        bool any = false;
        foreach (var s in opts.Series)
        {
            if (s is null || s.Axis != axis || s.Points is null) continue;
            foreach (var p in s.Points)
            {
                if (!p.Ok) continue;
                any = true;
                if (p.V < lo) lo = p.V;
                if (p.V > hi) hi = p.V;
            }
        }
        return any ? (lo, hi) : null;
    }
}
