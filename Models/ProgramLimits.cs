using System;
using System.Collections.Generic;
using System.Globalization;

namespace ParallelReactor.Models;

/// <summary>
/// 程序参数校验用的全局限值（取自 AppSettings；不直接引用 AppSettings，纯逻辑单测可链接）。
/// </summary>
/// <param name="TMaxSp">温度 SP 上限 ℃。</param>
/// <param name="OverTemp">超温报警值 ℃（加热目标必须严格低于它）。</param>
/// <param name="RpmMax">搅拌转速上限 rpm。</param>
/// <param name="PMaxSp">压力 SP 上限 psi。</param>
/// <param name="OverPressure">超压报警值 psi（升压目标须低于其 90%，同泄漏测试）。</param>
public readonly record struct ProgramLimitSettings(double TMaxSp, double OverTemp, double RpmMax, double PMaxSp, double OverPressure);

/// <summary>
/// 程序步骤参数与全局限值的校验（C4，纯逻辑）。编辑时（检查器 / 屏幕键盘范围）、下发时（TryRunProgram）、
/// 执行每一步前（RunStepAsync）三处共用同一套规则：
/// <list type="bullet">
/// <item>升温（定值）：35 ≤ 目标 ≤ TMaxSp 且 &lt; OverTemp；</item>
/// <item>升温（曲线）：至少 1 段；每段温度 0 ≤ T ≤ TMaxSp 且 &lt; OverTemp，斜率 0–50 ℃/min，保温 ≥ 0；</item>
/// <item>搅拌：0 ≤ 转速 ≤ RpmMax；</item>
/// <item>升压：1 psi ≤ 目标 ≤ PMaxSp 且 &lt; OverPressure × 0.9（与泄漏测试同一规则）。</item>
/// </list>
/// 其余步骤（吹扫 / 等待 / 反应 / 结束）不涉及加热、加压给定值，不在此校验（字段范围仍由 FieldDef 约束）。
/// </summary>
public static class ProgramLimits
{
    /// <summary>定值升温的最低目标温度（低于此温度不启动加热）。</summary>
    public const double HeatTempMin = 35;
    /// <summary>曲线斜率上限 ℃/min（同曲线编辑器）。</summary>
    public const double RateMax = 50;
    /// <summary>升压目标下限 psi（编辑器与校验同一下限，避免编辑器能输入 0、下发时才被拒绝）。</summary>
    public const double PressTargetMin = 1;
    /// <summary>升压目标相对超压报警值的比例上限（同泄漏测试）。</summary>
    public const double PressRatio = 0.9;

    /// <summary>
    /// 编辑器里加热目标的上限：min(TMaxSp, OverTemp − 1)。减 1℃ 是为了让屏幕键盘可输入的最大值严格低于超温报警值
    /// （校验本身只要求 &lt; OverTemp）。
    /// </summary>
    public static double HeatTempMax(ProgramLimitSettings l) => Math.Min(l.TMaxSp, l.OverTemp - 1);

    /// <summary>编辑器里升压目标的上限：min(PMaxSp, OverPressure × 0.9 − 0.1)。</summary>
    public static double PressMax(ProgramLimitSettings l) => Math.Min(l.PMaxSp, l.OverPressure * PressRatio - 0.1);

    /// <summary>编辑器里搅拌转速的上限。</summary>
    public static double RpmMaxAllowed(ProgramLimitSettings l) => l.RpmMax;

    /// <summary>
    /// 检查器数值字段的可输入范围：FieldDef 自身范围与全局限值取交集（屏幕键盘按此裁剪）。
    /// 交集为空时（例如 TMaxSp 被设得低于 35℃）返回 (min, min)，下发时由 <see cref="CheckStep"/> 拒绝。
    /// </summary>
    public static (double Min, double Max) FieldRange(StepType type, string key, double min, double max, ProgramLimitSettings l)
    {
        double lo = min, hi = max;
        switch (type, key)
        {
            case (StepType.Heat, "temp"): lo = Math.Max(lo, HeatTempMin); hi = Math.Min(hi, HeatTempMax(l)); break;
            case (StepType.Stir, "rpm"): hi = Math.Min(hi, RpmMaxAllowed(l)); break;
            case (StepType.Press, "pres"): lo = Math.Max(lo, PressTargetMin); hi = Math.Min(hi, PressMax(l)); break;
            case (StepType.End, "cool"): hi = Math.Min(hi, l.TMaxSp); break;   // 降温目标不是加热给定值，只按 SP 上限封顶
        }
        if (hi < lo) hi = lo;
        return (lo, hi);
    }

    /// <summary>把数值裁剪到 <see cref="FieldRange"/>（新建步骤的默认值用）。</summary>
    public static double ClampField(StepType type, string key, double v, double min, double max, ProgramLimitSettings l)
    {
        var (lo, hi) = FieldRange(type, key, min, max, l);
        return double.IsFinite(v) ? Math.Clamp(v, lo, hi) : lo;
    }

    /// <summary>单步校验：通过返回 null，否则返回原因（不含步骤序号，如「目标 300℃ 高于温度 SP 上限 200℃」）。</summary>
    public static string? CheckStep(StepSnapshot s, ProgramLimitSettings l)
    {
        switch (s.Type)
        {
            case StepType.Heat when s.IsCurve:
                if (s.Segments.Count == 0) return "升温曲线没有任何段";
                for (int i = 0; i < s.Segments.Count; i++)
                {
                    var (t, r, h) = s.Segments[i];
                    string seg = $"第 {i + 1} 段";
                    if (TempReason(t, l, 0) is { } why) return $"{seg}{why}";
                    if (!double.IsFinite(r) || r < 0 || r > RateMax) return $"{seg}斜率 {N(r)}℃/min 超出 0–{N(RateMax)}℃/min";
                    if (!double.IsFinite(h) || h < 0) return $"{seg}保温时长 {N(h)} min 无效";
                }
                return null;
            case StepType.Heat:
                return TempReason(s.Num("temp", 150), l, HeatTempMin);
            case StepType.Stir:
            {
                double rpm = s.Num("rpm", 1000);
                if (!double.IsFinite(rpm) || rpm < 0) return $"转速 {N(rpm)} rpm 无效";
                if (rpm > l.RpmMax) return $"转速 {N(rpm)} rpm 高于搅拌转速上限 {N(l.RpmMax)} rpm";
                return null;
            }
            case StepType.Press:
            {
                double p = s.Num("pres", 50);
                if (!double.IsFinite(p) || p < PressTargetMin) return $"目标压力 {N(p)} psi 无效（须不低于 {N(PressTargetMin)} psi）";
                if (p > l.PMaxSp) return $"目标 {N(p)} psi 高于压力 SP 上限 {N(l.PMaxSp)} psi";
                double cap = l.OverPressure * PressRatio;
                if (p >= cap) return $"目标 {N(p)} psi 不低于超压报警值的 90%（{N(cap)} psi）";
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>整段程序校验：返回全部违规项（如「第 02 步 升温：目标 300℃ 高于温度 SP 上限 200℃」），空 = 通过。</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<StepSnapshot> steps, ProgramLimitSettings l)
    {
        var errs = new List<string>();
        int i = 0;
        foreach (var s in steps)
        {
            i++;
            if (CheckStep(s, l) is { } why) errs.Add($"第 {i:00} 步 {s.Name}：{why}");
        }
        return errs;
    }

    private static string? TempReason(double t, ProgramLimitSettings l, double min)
    {
        if (!double.IsFinite(t)) return "目标温度无效";
        if (t < min) return $"目标 {N(t)}℃ 低于 {N(min)}℃，无法启动加热";
        if (t > l.TMaxSp) return $"目标 {N(t)}℃ 高于温度 SP 上限 {N(l.TMaxSp)}℃";
        if (t >= l.OverTemp) return $"目标 {N(t)}℃ 不低于超温报警值 {N(l.OverTemp)}℃";
        return null;
    }

    private static string N(double v) =>
        !double.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture)
        : v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture)
        : v.ToString("0.#", CultureInfo.InvariantCulture);
}
