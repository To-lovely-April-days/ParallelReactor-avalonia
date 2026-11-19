using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>
/// 程序参数校验用的全局限值（取自 AppSettings；不直接引用 AppSettings，纯逻辑单测可链接）。
/// </summary>
/// <param name="TMaxSp">温度 SP 上限 ℃。</param>
/// <param name="OverTemp">超温报警值 ℃（加热目标必须严格低于它）。</param>
/// <param name="RpmMax">搅拌转速上限 rpm（釜转速）。</param>
/// <param name="PMaxSp">压力 SP 上限 psi。</param>
/// <param name="OverPressure">超压报警值 psi（压力目标须低于其 90%，同泄漏测试）。</param>
public readonly record struct ProgramLimitSettings(double TMaxSp, double OverTemp, double RpmMax, double PMaxSp, double OverPressure);

/// <summary>下发前检查的结果（STEPS-SPEC §4）：Errors 拒绝下发，Warnings 只提示。</summary>
public sealed record PreRunResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// 程序步骤参数与全局限值的校验（C4 + STEPS-SPEC §1 / §4，纯逻辑）。编辑时（检查器 / 屏幕键盘范围）、远程替换（TryReplaceProgram）、
/// 下发时（RunGuard）、执行每一步前（RunStepAsync）共用同一套规则：
/// <list type="bullet">
/// <item>每个起作用的字段（visibleWhen 成立）都在 <see cref="StepSpecs"/> 声明的范围内，选项是表里的码；</item>
/// <item>「上限」：温度 ≤ min(TMaxSp, OverTemp − 1)（校验只要求 &lt; OverTemp），压力 ≤ PMaxSp 且 &lt; OverPressure × 0.9，转速 ≤ RpmMax；</item>
/// <item>跨字段：exchange.release ≤ fill − 1，react.minTime ≤ time；升温曲线 1–16 段，段温度同上限、斜率 0–50、保温 ≥ 0。</item>
/// </list>
/// 整段程序另有 <see cref="PreRun"/>：end 必须是最后一步且只有一个、先充压后升温的压力估算，以及只提示不拦的几条。
/// </summary>
public static class ProgramLimits
{
    /// <summary>定值升温的最低目标温度（低于此温度不启动加热）。</summary>
    public const double HeatTempMin = 35;
    /// <summary>曲线斜率上限 ℃/min（同曲线编辑器）。</summary>
    public const double RateMax = 50;
    /// <summary>压力目标相对超压报警值的比例上限（同泄漏测试）。</summary>
    public const double PressRatio = 0.9;
    /// <summary>估算用的大气压 psi（同 <see cref="LeakMath.AtmPsi"/>）。</summary>
    public const double AtmPsi = LeakMath.AtmPsi;
    /// <summary>没有前置升温时，充压时的釜温按室温估算 ℃。</summary>
    public const double RoomTempC = 25;

    /// <summary>
    /// 编辑器里加热目标的上限：min(TMaxSp, OverTemp − 1)。减 1℃ 是为了让屏幕键盘可输入的最大值严格低于超温报警值
    /// （校验本身只要求 &lt; OverTemp）。
    /// </summary>
    public static double HeatTempMax(ProgramLimitSettings l) => Math.Min(l.TMaxSp, l.OverTemp - 1);

    /// <summary>编辑器里压力目标的上限：min(PMaxSp, OverPressure × 0.9 − 0.1)（默认 458.9 psi ≈ 3.16 MPa）。</summary>
    public static double PressMax(ProgramLimitSettings l) => Math.Min(l.PMaxSp, l.OverPressure * PressRatio - 0.1);

    /// <summary>编辑器里搅拌转速的上限。</summary>
    public static double RpmMaxAllowed(ProgramLimitSettings l) => l.RpmMax;

    /// <summary>
    /// 检查器数值字段的可输入范围：FieldDef 声明范围与全局限值取交集（屏幕键盘按此裁剪）；sibling 给出同一步骤其他字段的当前值时，
    /// 再按跨字段规则收紧（exchange.release ≤ fill − 1，react.minTime ≤ time）。
    /// 交集为空时（例如 TMaxSp 被设得低于 35℃）返回 (min, min)，下发时由 <see cref="CheckStep"/> 拒绝。
    /// </summary>
    public static (double Min, double Max) FieldRange(StepType type, string key, double min, double max, ProgramLimitSettings l,
        Func<string, double>? sibling = null)
    {
        double lo = min, hi = max;
        var f = StepSpecs.TryGet(type, out var spec) ? spec.Field(key) : null;
        switch (f?.Limit ?? FieldLimit.None)
        {
            case FieldLimit.Temp:
                hi = Math.Min(hi, HeatTempMax(l));
                if (type == StepType.Heat) lo = Math.Max(lo, HeatTempMin);
                break;
            case FieldLimit.Pressure: hi = Math.Min(hi, PressMax(l)); break;
            case FieldLimit.Rpm: hi = Math.Min(hi, RpmMaxAllowed(l)); break;
        }
        if (sibling != null)
        {
            if (type == StepType.Exchange && key == "release") hi = Math.Min(hi, sibling("fill") - 1);
            if (type == StepType.React && key == "minTime") hi = Math.Min(hi, sibling("time"));
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
        if (s.Type.IsLegacy()) return $"旧格式步骤「{s.Type.Zh()}」须先转换（STEPS-SPEC §3）";
        if (!StepSpecs.TryGet(s.Type, out var spec)) return $"未知步骤类型「{s.Type}」";
        bool curve = s.IsCurve;
        foreach (var f in spec.Fields)
        {
            if (!StepSpecs.IsVisible(s.Type, f, s.Params)) continue;
            if (f.IsSel)
            {
                string code = s.Str(f.Key, f.DefaultSel);
                if (f.CodeOf(code) != code) return $"{f.Label}「{code}」不是可选的值";
                continue;
            }
            double v = s.Num(f.Key, f.Default ?? 0);
            if (!double.IsFinite(v)) return $"{f.Label}无效";
            switch (f.Limit)
            {
                case FieldLimit.Temp:
                    if ((s.Type == StepType.Heat ? TempReason(v, l, HeatTempMin) : HoldTempReason(v, l)) is { } tw) return tw;
                    continue;
                case FieldLimit.Pressure:
                    if (PressReason(f, v, l) is { } pw) return pw;
                    continue;
                case FieldLimit.Rpm:
                    if (v < 0) return $"转速 {N(v)} rpm 无效";
                    if (v > l.RpmMax) return $"转速 {N(v)} rpm 高于搅拌转速上限 {N(l.RpmMax)} rpm";
                    continue;
            }
            if (v < f.Min || v > f.Max) return $"{f.Label} {Fmt(f, v)} 超出 {Fmt(f, f.Min)}–{Fmt(f, f.Max)}";
            if (f.Integer && Math.Abs(v - Math.Round(v)) > 1e-9) return $"{f.Label} {N(v)} 须为整数";
        }
        if (curve)
        {
            if (s.Segments.Count == 0) return "升温曲线没有任何段";
            if (s.Segments.Count > TempProfile_MaxSegments) return $"升温曲线最多 {TempProfile_MaxSegments} 段";
            for (int i = 0; i < s.Segments.Count; i++)
            {
                var (t, r, h) = s.Segments[i];
                string seg = $"第 {i + 1} 段";
                if (TempReason(t, l, 0) is { } why) return $"{seg}{why}";
                if (!double.IsFinite(r) || r < 0 || r > RateMax) return $"{seg}斜率 {N(r)}℃/min 超出 0–{N(RateMax)}℃/min";
                if (!double.IsFinite(h) || h < 0) return $"{seg}保温时长 {N(h)} min 无效";
            }
        }
        if (s.Type == StepType.Exchange && s.Num("release", 2.9) > s.Num("fill", 72.5) - 1)
            return $"「放到」{Units.FormatP(s.Num("release", 2.9))} 须比「充到」{Units.FormatP(s.Num("fill", 72.5))} 低 1 psi 以上";
        if (s.Type == StepType.React && s.Num("minTime", 30) > s.Num("time", 480))
            return $"最短时间 {N(s.Num("minTime", 30))} min 不能超过最长时间 {N(s.Num("time", 480))} min";
        return null;
    }

    /// <summary>同 Models/TempProfile.cs 的 MaxSegments（那个文件含 Avalonia 类型，这里不引用）。</summary>
    private const int TempProfile_MaxSegments = 16;

    /// <summary>整段程序逐步校验：返回全部违规项（如「第 02 步 升温：目标 300℃ 高于温度 SP 上限 200℃」），空 = 通过。</summary>
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

    /// <summary>
    /// 下发前检查（STEPS-SPEC §4，HMI 部分；「同批几路第一个 stir 转速一致」只有 PC 端检查）：
    /// <list type="bullet">
    /// <item>拒绝：逐步校验（<see cref="Validate"/>）；end 不在最后或不止一个；先充压后升温且估算压力超过上限。</item>
    /// <item>提示：反应前没有充压（吸气量不计量）；先充压后升温（估算升温后的压力，稳压不会放气）；没有放空（结束时釜里可能带压）。</item>
    /// </list>
    /// 估算 = 目标绝压 × (T目标 + 273.15) / (T充压时 + 273.15) − 大气压；T充压时 = 充压前最近一个升温的目标（没有按 25 ℃），
    /// T目标 = 充压之后、下一次换气（充压 / 置换 / 放空 / 气密检查）之前升温目标的最高值。
    /// </summary>
    public static PreRunResult PreRun(IReadOnlyList<StepSnapshot> steps, ProgramLimitSettings l)
    {
        var errors = new List<string>(Validate(steps, l));
        var warnings = new List<string>();
        int ends = steps.Count(s => s.Type == StepType.End);
        if (ends == 0) errors.Add("程序没有「结束」步骤（必须是最后一步）");
        else if (ends > 1) errors.Add($"程序有 {ends} 个「结束」步骤（只能有一个，且必须是最后一步）");
        else if (steps.Count > 0 && steps[^1].Type != StepType.End) errors.Add("「结束」必须是最后一步");

        double? heatBefore = null;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            switch (s.Type)
            {
                case StepType.Heat:
                    heatBefore = HeatTarget(s);
                    break;
                case StepType.React:
                {
                    // 吸气计量在充压完成后开始，遇到气密检查 / 置换 / 放空停止：往前找最近一个换气步骤，必须是充压
                    var gasStep = steps.Take(i).LastOrDefault(x => x.Type is StepType.Press or StepType.Leak or StepType.Exchange or StepType.Vent);
                    if (gasStep?.Type != StepType.Press)
                        warnings.Add($"第 {i + 1:00} 步 反应：前面没有充压，吸气量不计量（只按最长时间结束）");
                    break;
                }
                case StepType.Press:
                {
                    double target = s.Num("pres", 290.1);
                    double tAt = heatBefore ?? RoomTempC;
                    double tMax = double.NegativeInfinity;
                    for (int j = i + 1; j < steps.Count; j++)
                    {
                        var n = steps[j];
                        if (n.Type is StepType.Press or StepType.Leak or StepType.Exchange or StepType.Vent or StepType.End) break;
                        if (n.Type == StepType.Heat && HeatTarget(n) is double ht) tMax = Math.Max(tMax, ht);
                    }
                    if (tMax > tAt)
                    {
                        double est = EstimateAfterHeat(target, tAt, tMax);
                        string msg = $"第 {i + 1:00} 步 充压在升温之前：升温后压力会高于目标（估算 {Units.FormatP(est)}），稳压不会放气";
                        double cap = PressMax(l);
                        if (est > cap) errors.Add($"{msg}；超过压力上限 {Units.FormatP(cap)}");
                        else warnings.Add(msg);
                    }
                    break;
                }
            }
        }
        if (steps.Count > 0 && !steps.Any(s => s.Type == StepType.Vent))
            warnings.Add("程序里没有「放空」：结束时釜里可能带压");
        return new PreRunResult(errors, warnings);
    }

    /// <summary>升温后的压力估算（psi 表压）：等容理想气体。</summary>
    public static double EstimateAfterHeat(double targetPsig, double tAtC, double tAfterC)
        => (targetPsig + AtmPsi) * (tAfterC + 273.15) / (tAtC + 273.15) - AtmPsi;

    /// <summary>升温步骤的目标温度：定值取 temp，曲线取最后一段。</summary>
    public static double? HeatTarget(StepSnapshot s)
        => s.IsCurve ? (s.Segments.Count > 0 ? s.Segments[^1].Temp : null) : s.Num("temp", 80);

    private static string? TempReason(double t, ProgramLimitSettings l, double min)
    {
        if (!double.IsFinite(t)) return "目标温度无效";
        if (t < min) return $"目标 {N(t)}℃ 低于 {N(min)}℃，无法启动加热";
        if (t > l.TMaxSp) return $"目标 {N(t)}℃ 高于温度 SP 上限 {N(l.TMaxSp)}℃";
        if (t >= l.OverTemp) return $"目标 {N(t)}℃ 不低于超温报警值 {N(l.OverTemp)}℃";
        return null;
    }

    /// <summary>保持步骤的目标温度（不是加热给定值）：0 ≤ T ≤ TMaxSp 且 &lt; OverTemp。</summary>
    private static string? HoldTempReason(double t, ProgramLimitSettings l)
    {
        if (t < 0) return $"目标温度 {N(t)}℃ 无效";
        if (t > l.TMaxSp) return $"目标 {N(t)}℃ 高于温度 SP 上限 {N(l.TMaxSp)}℃";
        if (t >= l.OverTemp) return $"目标 {N(t)}℃ 不低于超温报警值 {N(l.OverTemp)}℃";
        return null;
    }

    /// <summary>压力类字段：不低于声明下限，不高于 PMaxSp，且低于超压报警值的 90%（文字按压力单位）。</summary>
    private static string? PressReason(FieldDef f, double p, ProgramLimitSettings l)
    {
        if (p < f.Min) return $"{f.Label} {Units.FormatP(p)} 无效（须不低于 {Units.FormatP(f.Min)}）";
        if (p > l.PMaxSp) return $"{f.Label} {Units.FormatP(p)} 高于压力 SP 上限 {Units.FormatP(l.PMaxSp)}";
        double cap = l.OverPressure * PressRatio;
        if (p >= cap) return $"{f.Label} {Units.FormatP(p)} 不低于超压报警值的 90%（{Units.FormatP(cap)}）";
        return null;
    }

    private static string Fmt(FieldDef f, double v)
        => f.Pressure ? (f.Unit == "psi/h" ? StepSpecs.PRate(v) : Units.FormatP(v)) : $"{N(v)}{(f.Unit is { Length: > 0 } u ? " " + u : "")}";

    private static string N(double v) =>
        !double.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture)
        : v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture)
        : v.ToString("0.###", CultureInfo.InvariantCulture);
}
