using System;
using System.Collections.Generic;

namespace ParallelReactor.Models;

/// <summary>最小二乘直线拟合结果：y = Intercept + Slope·x。点数 &lt; 2 或 x 无变化时 Slope/R2 为 NaN。</summary>
public readonly record struct LineFit(double Slope, double Intercept, double R2, int N)
{
    public bool Valid => !double.IsNaN(Slope);
}

/// <summary>
/// 泄漏测试的逐点数据处理（纯逻辑，单元测试直接链接）：最小二乘拟合 + 温度补偿到绝对压力。
/// <para>
/// 温度补偿按等容理想气体：P_corr = P_abs × T_ref / T（绝对压力、绝对温度 K），
/// T_ref = 测量窗口起点的釜温。大气压取标准大气压 101.325 kPa（= <see cref="LeakMath.AtmPsi"/> 14.696 psi）——
/// 系统没有气压计读数，表压换算绝压只能用标准值；它只带来常数偏移，不影响压降斜率。
/// </para>
/// </summary>
public static class LeakFit
{
    public const double KpaPerPsi = 6.894757;
    public const double AtmKpa = 101.325;
    public const double Kelvin = 273.15;

    /// <summary>最小二乘拟合（去均值累加，避免大数相减丢精度）。</summary>
    public static LineFit Fit(IReadOnlyList<(double X, double Y)> pts)
    {
        int n = pts.Count;
        if (n < 2) return new LineFit(double.NaN, double.NaN, double.NaN, n);
        double mx = 0, my = 0;
        foreach (var (x, y) in pts) { mx += x; my += y; }
        mx /= n; my /= n;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var (x, y) in pts)
        {
            sxx += (x - mx) * (x - mx);
            sxy += (x - mx) * (y - my);
            syy += (y - my) * (y - my);
        }
        if (sxx < 1e-12) return new LineFit(double.NaN, double.NaN, double.NaN, n);
        double slope = sxy / sxx;
        // 所有点 y 完全相同：直线完美拟合（斜率 0），R² 记 1
        double r2 = syy < 1e-24 ? 1.0 : sxy * sxy / (sxx * syy);
        return new LineFit(slope, my - slope * mx, r2, n);
    }

    /// <summary>按 (秒, 值) 拟合，返回斜率换算为「每小时」的拟合结果（截距不变）。</summary>
    public static LineFit FitPerHour(IReadOnlyList<(double Sec, double Y)> pts)
    {
        var f = Fit(pts);
        return f with { Slope = f.Slope * 3600.0 };
    }

    /// <summary>表压 psig → 绝压 kPa（标准大气压）。</summary>
    public static double PsigToAbsKpa(double psig) => psig * KpaPerPsi + AtmKpa;

    /// <summary>温度补偿后的绝对压力 kPa：P_abs × (T_ref + 273.15) / (T + 273.15)。温度无效时返回 NaN。</summary>
    public static double CorrectedAbsKpa(double psig, double tC, double tRefC)
    {
        if (double.IsNaN(tC) || double.IsNaN(tRefC) || tC + Kelvin <= 0) return double.NaN;
        return PsigToAbsKpa(psig) * (tRefC + Kelvin) / (tC + Kelvin);
    }
}
