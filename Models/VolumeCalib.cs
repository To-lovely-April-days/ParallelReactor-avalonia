using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>
/// 釜有效容积标定（气体膨胀法，惰性气体、常温）的计算。纯逻辑：测试工程直接链接本文件。
/// <para>
/// 做法：釜内压力 P(k−1)（SV 关）→ 只开惰性气源给总管充气（总管到减压阀出口压力 Preg）→ 关气源 → 只开该路 SV，
/// 总管里的气放进釜里，平衡后读 P(k) → 关 SV。重复 N 次。总管体积 Vm、釜气相体积 Vg 时，每一步：
/// Vm·(Preg − Pk) = Vg·(Pk − P(k−1))，即 Pk = (1 − f)·P(k−1) + f·Preg，f = Vm / (Vm + Vg)。
/// 用 N 组 (P(k−1), Pk) 做直线拟合：斜率 a = 1 − f，所以 Vg / Vm = a / (1 − a)。
/// </para>
/// <para>
/// 好处：不需要知道减压阀的出口压力（截距里），进气单向阀的开启压力也只改截距、不影响斜率；
/// 全部读数都来自该路自己的压力变送器（总管没有变送器）；用压差，表压 / 绝压都一样。
/// </para>
/// <para>
/// 总管体积未知，所以第一次要做两遍：空釜一遍（比值 r1 = Veff / Vm）、加入已知体积 L 的水再做一遍（r2 = (Veff − L) / Vm），
/// 解出 Vm = L / (r1 − r2)、Veff = r1·Vm。Veff = 釜容积 + 该路 SV 以下的管路（有效容积）。总管体积记下后，其他通道空釜做一遍即可：Veff = r·Vm。
/// </para>
/// 局限：按等温计算（每步 SV 打开后等压力稳定再读）；各通道从总管到 SV 的支路长度不同，按同一个 Vm 处理。
/// </summary>
public static class VolumeCalib
{
    /// <summary>每次标定的充放次数。</summary>
    public const int Steps = 6;
    /// <summary>拟合至少要这么多组数据。</summary>
    public const int MinSteps = 4;
    /// <summary>合理的 f = Vm / (Vm + Vg) 范围：太小说明几乎没进气（气源没开 / 减压阀没压力 / 单向阀卡住），太大说明釜几乎没有容积。</summary>
    public const double MinFraction = 0.01, MaxFraction = 0.9;
    /// <summary>拟合优度下限：读数跳动太大时不给结果。</summary>
    public const double MinR2 = 0.995;
    /// <summary>每步压力至少上升这么多 psi，否则压差相对噪声太小。</summary>
    public const double MinRisePsi = 0.5;
    /// <summary>有效容积的合理范围 mL（超出说明加水体积填错或测量有问题）。</summary>
    public const double MinVolumeMl = 2, MaxVolumeMl = 2000;

    /// <summary>一次标定（一遍 N 步）的拟合结果。</summary>
    public sealed record Fit(double Slope, double Intercept, double R2, int N)
    {
        /// <summary>f = Vm / (Vm + Vg) = 1 − 斜率。</summary>
        public double Fraction => 1 - Slope;
        /// <summary>釜气相体积 / 总管体积 = 斜率 / (1 − 斜率)。</summary>
        public double Ratio => Slope / (1 - Slope);
        /// <summary>减压阀（扣掉单向阀开启压力后）的等效出口压力 psi = 截距 / f。</summary>
        public double SupplyPsi => Intercept / Fraction;
    }

    /// <summary>
    /// 拟合一遍的数据（每组 = 这一步放气前、后的釜压 psi）。数据不足、不合理时返回 (null, 原因)。
    /// </summary>
    public static (Fit? Fit, string? Why) FitSteps(IReadOnlyList<(double Before, double After)> steps)
    {
        if (steps.Count < MinSteps) return (null, $"有效数据只有 {steps.Count} 组（至少 {MinSteps} 组）");
        if (steps.Any(s => !double.IsFinite(s.Before) || !double.IsFinite(s.After))) return (null, "压力读数无效");
        double minRise = steps.Min(s => s.After - s.Before);
        if (minRise < MinRisePsi)
            return (null, $"有一步压力只升了 {minRise:0.##} psi · 惰性气体减压阀压力太低、气源没开，或已接近减压阀压力");
        int n = steps.Count;
        double mx = steps.Average(s => s.Before), my = steps.Average(s => s.After);
        double sxx = steps.Sum(s => (s.Before - mx) * (s.Before - mx));
        double sxy = steps.Sum(s => (s.Before - mx) * (s.After - my));
        double syy = steps.Sum(s => (s.After - my) * (s.After - my));
        if (sxx <= 0 || syy <= 0) return (null, "压力没有变化");
        double a = sxy / sxx, b = my - a * mx;
        double r2 = sxy * sxy / (sxx * syy);
        var fit = new Fit(a, b, r2, n);
        if (fit.Fraction < MinFraction || fit.Fraction > MaxFraction)
            return (null, $"每步进气比例 {fit.Fraction:0.###} 不合理（应在 {MinFraction}–{MaxFraction}）");
        if (r2 < MinR2) return (null, $"读数一致性不够（R² = {r2:0.0000} < {MinR2}）· 可能有泄漏或温度没稳定");
        return (fit, null);
    }

    /// <summary>两遍（空釜 r1、加水 L mL 后 r2）解出总管体积与有效容积。不合理时返回 (null, null, 原因)。</summary>
    public static (double? ManifoldMl, double? EffectiveMl, string? Why) SolveTwoRuns(double rEmpty, double rWater, double waterMl)
    {
        if (!(waterMl > 0)) return (null, null, "加水体积必须大于 0");
        if (!(rEmpty > rWater) || rWater <= 0) return (null, null, "加水后的比值没有变小 · 检查是否真的加了水、加水体积是否填对");
        double vm = waterMl / (rEmpty - rWater);
        double veff = rEmpty * vm;
        if (veff < MinVolumeMl || veff > MaxVolumeMl || vm < 0.1 || vm > MaxVolumeMl)
            return (null, null, $"结果不合理（总管 {vm:0.#} mL、有效容积 {veff:0.#} mL）· 检查加水体积");
        if (waterMl >= veff) return (null, null, $"加水 {waterMl:0.#} mL 不小于算出的有效容积 {veff:0.#} mL · 检查加水体积");
        return (vm, veff, null);
    }

    /// <summary>已知总管体积时，一遍空釜的比值 → 有效容积。</summary>
    public static (double? EffectiveMl, string? Why) SolveOneRun(double rEmpty, double manifoldMl)
    {
        if (!(manifoldMl > 0)) return (null, "还没有总管体积 · 先做一次「空釜 + 加水」两遍标定");
        double veff = rEmpty * manifoldMl;
        if (veff < MinVolumeMl || veff > MaxVolumeMl) return (null, $"结果不合理（有效容积 {veff:0.#} mL）");
        return (veff, null);
    }
}
