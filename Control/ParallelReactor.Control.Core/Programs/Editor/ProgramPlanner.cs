using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>预计曲线的一个点：分钟、温度 ℃、压力 MPa。</summary>
public readonly record struct PlanPoint(double M, double T, double P);

/// <summary>一步的起止（分钟）。</summary>
public readonly record struct PlanBound(double M0, double M1, int I);

/// <summary>一个程序的估计：折线点、每步起止、总时长（分钟）。</summary>
public sealed record ProgramPlanResult(IReadOnlyList<PlanPoint> Points, IReadOnlyList<PlanBound> Bounds, double Total);

/// <summary>
/// 程序的估计时长与预计温度 / 压力曲线（样稿 progPlan，逐项照搬）：从 25 ℃ / 0 MPa 开始；
/// 升温 |目标 − 当前| / 3.5 ℃/min + 等待平衡（曲线升温按每段速率 + 保温）；升压 3 min；吹扫 次数 × (充 + 排) 秒；搅拌 = 时长；等待 / 反应 = 最长时间；
/// 结束 = 降到「先降温至」按 5 ℃/min，密封保持压力、其它方式泄到 0。只是给人看的估计，不是设备的计时（真机的预计结束另见 EtaEstimator）。
/// </summary>
public static class ProgramPlanner
{
    public const double HeatRate = 3.5, CoolRate = 5, PressMin = 3, StartTemp = 25;

    public static ProgramPlanResult Plan(IReadOnlyList<PcStep> steps)
    {
        double T = StartTemp, P = 0, m = 0;
        var pts = new List<PlanPoint> { new(0, StartTemp, 0) };
        var bounds = new List<PlanBound>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            double m0 = m;
            switch (s.Type)
            {
                case StepType.Heat:
                    if (s.S("mode") == "curve" && s.Segments.Count > 0)
                    {
                        foreach (var g in s.Segments)
                        {
                            m += g.Rate > 0 ? Math.Abs(g.Temp - T) / g.Rate : 0;
                            T = g.Temp;
                            pts.Add(new(m, T, P));
                            m += Math.Max(0, g.Hold);
                            pts.Add(new(m, T, P));
                        }
                    }
                    else
                    {
                        double temp = Fin(s.N("temp"), T);
                        m += Math.Abs(temp - T) / HeatRate;
                        T = temp;
                        pts.Add(new(m, T, P));
                        m += Math.Max(0, Fin(s.N("wait"), 0));
                        pts.Add(new(m, T, P));
                    }
                    break;
                case StepType.Press:
                    m += PressMin;
                    P = Fin(s.N("pres"), P);
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Purge:
                    m += Fin(s.N("cycles"), 0) * (Fin(s.N("fill"), 0) + Fin(s.N("vent"), 0)) / 60;
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Stir:
                    m += Math.Max(0, Fin(s.N("dur"), 0));
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Wait:
                case StepType.React:
                    m += Math.Max(0, Fin(s.N("time"), 0));
                    pts.Add(new(m, T, P));
                    break;
                case StepType.End:
                {
                    double cool = Fin(s.N("cool"), T);
                    m += Math.Max(0, T - cool) / CoolRate;
                    T = Math.Min(T, cool);
                    if (s.S("mode", "seal") != "seal") P = 0;
                    pts.Add(new(m, T, P));
                    break;
                }
            }
            bounds.Add(new PlanBound(m0, m, i));
        }
        return new ProgramPlanResult(pts, bounds, m);
    }

    private static double Fin(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    /// <summary>样稿 hm：'2 h 01 min' / '45 min'（四舍五入到分钟）。</summary>
    public static string Hm(double minutes)
    {
        long m = (long)Math.Round(double.IsFinite(minutes) ? minutes : 0, MidpointRounding.AwayFromZero);
        return m >= 60 ? (m / 60).ToString(CultureInfo.InvariantCulture) + " h " + (m % 60).ToString("00", CultureInfo.InvariantCulture) + " min"
            : m.ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>
    /// 步骤列表一行的摘要（样稿 stepDisp 的 rows，两项）：升温「目标温度 150.0 °C」「等待平衡 15 min」……
    /// </summary>
    public static IReadOnlyList<(string K, string V)> Summary(PcStep s, ProgramSchema schema)
    {
        string L(string key) => schema.Field(s.Type, key) is { } f ? f.LabelOf(s.S(key)) : s.S(key);
        string N1(double v) => JsNumber.ToFixed(v, 1);
        string N0(double v) => double.IsFinite(v) ? JsNumber.ToString(Math.Round(v, 1)) : Fmt.Dash;
        return s.Type switch
        {
            StepType.Heat when s.S("mode") == "curve" => new[] { ("曲线升温", s.Segments.Count + " 段"), ("终温", s.Segments.Count > 0 ? N1(s.Segments[^1].Temp) + " °C" : Fmt.Dash) },
            StepType.Heat => new[] { ("目标温度", N1(s.N("temp")) + " °C"), ("等待平衡", N0(s.N("wait")) + " min") },
            StepType.Press => new[] { ("目标压力", PressureDisplay.InputU(s.N("pres"))), ("压力稳压", L("regulate")) },
            StepType.React => new[] { ("结束条件", L("endBy")), ("最长时间", N0(s.N("time")) + " min") },
            StepType.End => new[] { ("先降温至", N1(s.N("cool")) + " °C"), ("结束方式", L("mode")) },
            StepType.Purge => new[] { ("循环次数", N0(s.N("cycles")) + " 次"), ("充气 / 排气", N0(s.N("fill")) + " / " + N0(s.N("vent")) + " 秒") },
            StepType.Stir => new[] { ("转速", N0(s.N("rpm")) + " rpm"), ("搅拌时长", N0(s.N("dur")) + " min") },
            StepType.Wait => new[] { ("条件", L("mode")), ("最长时间", N0(s.N("time")) + " min") },
            _ => Array.Empty<(string, string)>(),
        };
    }
}

/// <summary>一条待改项（样稿 progIssues）：第几步、哪个字段、文字。</summary>
public sealed record ProgramIssue(int I, string Key, string Msg);

/// <summary>
/// 程序的校验（样稿 progIssues，范围取设备的 schema）：数值超出范围（含没填）、选了 HMI 说现在不可用的选项、曲线升温没有段、搅拌转速超出釜转速上限。
/// 不是报警：程序库里是「N 处待改」的琥珀标签，「下发并运行」要先改掉。
/// </summary>
public static class ProgramIssues
{
    public static IReadOnlyList<ProgramIssue> Of(IReadOnlyList<PcStep> steps, ProgramSchema schema, string modelLabel)
    {
        var out_ = new List<ProgramIssue>();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var spec = schema.Of(s.Type);
            if (spec is null) { out_.Add(new ProgramIssue(i, "", Step(i, s) + "：设备不支持这种步骤")); continue; }
            foreach (var f in spec.Fields)
            {
                if (f.IsSelect)
                {
                    var code = s.S(f.Key, f.Options[0].Code);
                    if (f.Options.FirstOrDefault(o => o.Code == code) is { Available: false } bad)
                        out_.Add(new ProgramIssue(i, f.Key, Step(i, s) + "：" + f.Label + "「" + bad.Label + "」" + (bad.Reason ?? "设备不支持")));
                    continue;
                }
                if (s.Type == StepType.Heat && s.S("mode") == "curve" && f.Key is "temp" or "wait") continue;
                if (s.Type == StepType.React && f.Key == "mmol" && s.S("endBy", "time") == "time") continue;
                double v = s.N(f.Key);
                if (f.Bad(v))
                    out_.Add(new ProgramIssue(i, f.Key, Step(i, s) + "：" + f.Label + " " + (double.IsFinite(v) ? f.Num(v) + " " + f.ShowUnit : "未填") + " 超出 " + modelLabel + " 范围 " + f.RangeText + " " + f.ShowUnit));
            }
            if (s.Type == StepType.Heat && s.S("mode") == "curve")
            {
                if (s.Segments.Count == 0) out_.Add(new ProgramIssue(i, "segments", Step(i, s) + "：曲线升温至少要 1 段"));
                var segF = spec.SegmentFields ?? Array.Empty<FieldSpec>();
                for (int j = 0; j < s.Segments.Count; j++)
                {
                    var g = s.Segments[j];
                    foreach (var f in segF)
                    {
                        double v = f.Key switch { "temp" => g.Temp, "rate" => g.Rate, _ => g.Hold };
                        if (f.Bad(v)) out_.Add(new ProgramIssue(i, "segments", Step(i, s) + "：第 " + (j + 1) + " 段" + f.Label + " " + f.Num(v) + " 超出范围 " + f.RangeText + " " + f.ShowUnit));
                    }
                }
            }
        }
        if (steps.Count == 0) out_.Add(new ProgramIssue(-1, "", "程序没有步骤"));
        return out_;
    }

    private static string Step(int i, PcStep s) => "第 " + (i + 1).ToString("00", CultureInfo.InvariantCulture) + " 步 " + s.Name;
}
