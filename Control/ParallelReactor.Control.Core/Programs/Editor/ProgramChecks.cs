using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>下发前检查的级别（STEPS-SPEC §4）：Reject = 拒绝下发（算待改项）；Warn = 提示，不拦。</summary>
public enum CheckLevel { Reject, Warn }

/// <summary>一条检查：级别、代码（end / nogas / heatafter / novent / rpm / release / mintime）、第几步（-1 = 整个程序）、文字。</summary>
public sealed record ProgramCheck(CheckLevel Level, string Code, int I, string Msg);

/// <summary>
/// 下发前检查（STEPS-SPEC §4，设计稿「六、下发前检查」）—— 范围检查另见 <see cref="ProgramIssues"/>（它把这里的「拒绝」也算进待改项）：
/// <list type="bullet">
/// <item>拒绝：结束不在最后或不止一个；先充压后升温、估算的压力超过上限。</item>
/// <item>提示：反应前面没有充压（吸气量不计量）；先充压后升温（写估算的压力）；没有放空（结束时釜里可能带压）。</item>
/// <item>同一批启动的几路里第一个搅拌的转速不一致（只有 PC 检查，<see cref="RpmMismatch"/>）：拒绝，写明哪几路。</item>
/// </list>
/// 纯逻辑：编辑器（待改项 + 提示）、预运行检查对话框（单路 / 条件矩阵）、下发前的最后一道检查都用它。
/// </summary>
public static class ProgramChecks
{
    /// <summary>1 atm（MPa）：表压 ↔ 绝压。</summary>
    public const double AtmMpa = 0.101325;

    /// <summary>升温之前的温度（程序开始时按室温 25 ℃）。</summary>
    public const double StartTemp = 25;

    /// <summary>先充压后升温：升温后的压力估算（表压 MPa）= 目标绝压 × (T 目标 + 273.15) / (T 充压时 + 273.15) − 大气压。</summary>
    public static double PressAfterHeat(double targetMpa, double tPress, double tHeat) =>
        (targetMpa + AtmMpa) * (tHeat + 273.15) / (tPress + 273.15) - AtmMpa;

    /// <summary>升温步骤的目标温度（曲线 = 最后一段）；没有为 null。</summary>
    public static double? HeatTarget(PcStep s) =>
        s.Type != StepType.Heat ? null : s.S("mode") == "curve" ? (s.Segments.Count > 0 ? s.Segments[^1].Temp : null) : double.IsFinite(s.N("temp")) ? s.N("temp") : null;

    /// <summary>
    /// 一个程序的结构检查（不含范围）。<paramref name="schema"/> 给压力上限（充压目标的上限：先充压后升温的估算超过它时拒绝）。
    /// </summary>
    public static IReadOnlyList<ProgramCheck> Of(IReadOnlyList<PcStep> steps, ProgramSchema schema)
    {
        var o = new List<ProgramCheck>();
        if (steps.Count == 0) return o;
        // 结束：必须是最后一步，且只能有一个
        int ends = steps.Count(s => s.Type == StepType.End);
        if (ends != 1 || steps[^1].Type != StepType.End)
            o.Add(new ProgramCheck(CheckLevel.Reject, "end", -1, ends == 0 ? "程序没有「结束」步骤 · 结束必须是最后一步" : ends > 1 ? "「结束」不止一个 · 只能有一个，且在最后" : "「结束」不在最后 · 结束必须是最后一步"));
        var pf = schema.Field(StepType.Press, "pres");
        double temp = StartTemp;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (HeatTarget(s) is double ht) temp = ht;
            // 跨字段（同 HMI 的 ProgramLimits：exchange.release ≤ fill − 1 psi，react.minTime ≤ time）—— 不先查，HMI 下发时才拒绝（端到端核对发现）
            if (s.Type == StepType.Exchange && Units.MpaToPsi(s.N("release", 0)) > Units.MpaToPsi(s.N("fill", 0.5)) - 1 + 1e-9)
                o.Add(new ProgramCheck(CheckLevel.Reject, "release", i, Step(i, s) + "「放到」" + PressureDisplay.InputU(s.N("release", 0)) + " 须比「充到」" + PressureDisplay.InputU(s.N("fill", 0.5)) + " 低 1 psi 以上"));
            if (s.Type == StepType.React && s.N("minTime", 0) > s.N("time", 480) + 1e-9)
                o.Add(new ProgramCheck(CheckLevel.Reject, "mintime", i, Step(i, s) + "「最短时间」" + JsNumber.ToString(s.N("minTime", 0)) + " min 不能超过「最长时间」" + JsNumber.ToString(s.N("time", 480)) + " min"));
            if (s.Type == StepType.React && !MeteredBefore(steps, i))
                o.Add(new ProgramCheck(CheckLevel.Warn, "nogas", i, Step(i, s) + "前面没有充压 · 吸气量不计量"));
            if (s.Type == StepType.Press && double.IsFinite(s.N("pres")))
            {
                // 后面（放掉压力之前）有更高的升温：升温后压力会高于目标，稳压不会放气
                double? hot = null;
                for (int j = i + 1; j < steps.Count; j++)
                {
                    var n = steps[j];
                    if (n.Type is StepType.Press or StepType.Vent or StepType.Exchange or StepType.Leak or StepType.End) break;   // 下一次换气之前（同 HMI 的 PreRun）
                    if (HeatTarget(n) is double t2 && t2 > temp + 1e-9 && (hot is null || t2 > hot)) hot = t2;
                }
                if (hot is double th)
                {
                    double est = PressAfterHeat(s.N("pres"), temp, th);
                    // 超上限：按 HMI 的压力上限（psi，min(PMaxSp, 超压 × 0.9 − 0.1)）比，不经界面单位的取整 —— 与 HMI 的下发前检查给同一个结论
                    double capPsi = pf?.Psi is { } lim ? lim.Max : pf != null ? Units.MpaToPsi(pf.Max) : double.PositiveInfinity;
                    bool over = Units.MpaToPsi(est) > capPsi + 1e-9;
                    string estText = PressureDisplay.InputU(est);
                    o.Add(over
                        ? new ProgramCheck(CheckLevel.Reject, "heatafter", i, Step(i, s) + "在升温之前 · 升温到 " + JsNumber.ToString(th) + " °C 后压力约 " + estText + "，超过上限 " + (pf != null ? JsNumber.ToString(Math.Round(pf.ShowMax, pf.ShowDp)) + " " + pf.ShowUnit : PressureDisplay.InputU(Units.PsiToMpa(capPsi))) + " · 先升温、到位后再充压")
                        : new ProgramCheck(CheckLevel.Warn, "heatafter", i, Step(i, s) + "在升温之前 · 升温后压力会高于目标（估算 " + estText + "），稳压不会放气"));
                }
            }
        }
        if (!steps.Any(s => s.Type == StepType.Vent))
            o.Add(new ProgramCheck(CheckLevel.Warn, "novent", -1, "程序里没有放空 · 结束时釜里可能带压"));
        return o;
    }

    /// <summary>第 i 步（反应）开始时吸气计量有效：往前找，先遇到充压 = 有效；先遇到气密 / 置换 / 放空 / 程序开头 = 无效（STEPS-SPEC §2）。</summary>
    public static bool MeteredBefore(IReadOnlyList<PcStep> steps, int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            var t = steps[j].Type;
            if (t == StepType.Press) return true;
            if (t is StepType.Leak or StepType.Exchange or StepType.Vent) return false;
        }
        return false;
    }

    /// <summary>程序里第一个搅拌的转速（釜转速）；没有搅拌步骤为 null。</summary>
    public static double? FirstRpm(IReadOnlyList<PcStep> steps) =>
        steps.FirstOrDefault(s => s.Type == StepType.Stir) is { } st && double.IsFinite(st.N("rpm")) ? st.N("rpm") : null;

    /// <summary>
    /// 同一批启动的几路（一台设备一台电机）：第一个搅拌的转速不一致时返回说明（按转速分组写明是哪几路：「RV1–RV4 400 rpm；RV5 300 rpm」），一致（或都没有搅拌）为 null。
    /// </summary>
    public static string? RpmMismatch(IEnumerable<(int N, IReadOnlyList<PcStep> Steps)> batch)
    {
        var by = new SortedDictionary<double, List<int>>();
        foreach (var (n, steps) in batch)
        {
            if (FirstRpm(steps) is not double r) continue;
            if (!by.TryGetValue(r, out var l)) by[r] = l = new List<int>();
            l.Add(n);
        }
        if (by.Count < 2) return null;
        return "一台电机带全部釜，同时启动的几路转速必须一样：" + string.Join("；", by.OrderBy(x => x.Value.Min()).Select(x => MatrixPlan.ChList(x.Value) + " " + JsNumber.ToString(x.Key) + " rpm"));
    }

    private static string Step(int i, PcStep s) => "第 " + (i + 1).ToString("00", CultureInfo.InvariantCulture) + " 步 " + s.Name;
}
