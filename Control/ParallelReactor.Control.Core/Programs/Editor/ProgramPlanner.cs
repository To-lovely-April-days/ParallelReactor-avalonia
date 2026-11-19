using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>预计曲线的一个点：分钟、温度 ℃、压力 MPa。</summary>
public readonly record struct PlanPoint(double M, double T, double P);

/// <summary>一步的起止（分钟）与估计的标注（按条件结束 =「约」，反应 =「≤」）。</summary>
public readonly record struct PlanBound(double M0, double M1, int I, StepMark Mark = StepMark.None);

/// <summary>一个程序的估计：折线点、每步起止、总时长（分钟）。</summary>
public sealed record ProgramPlanResult(IReadOnlyList<PlanPoint> Points, IReadOnlyList<PlanBound> Bounds, double Total);

/// <summary>
/// 程序的估计时长与预计温度 / 压力曲线（STEPS-SPEC §5，HMI 用同一套估算，单位都是分钟）：
/// 气密 = 稳定 + 保压 + 1；置换 = (先吹总管 × 9 + 次数 × 20) / 60；搅拌 0；定值升温 = |目标 − 25| / (速率 &gt; 0 ? 速率 : 3.5) + 平衡；曲线升温 = 曲线总时长；
/// 充压 3；保持按时间 = 时间，按条件 = min(最长等待, 15)（标「约」）；反应 = 最长时间（标「≤」）；放空 = 1 + 之后置换 × 20 / 60；结束 0。
/// 曲线：从 25 ℃ / 0 MPa 开始；充压到目标，气密到试验压力（之后放空回 0），置换 / 放空回到「放到」的压力，保持到温度 ≤ x 时降到 x。
/// 只是给人看的估计，不是设备的计时（真机的预计结束另见 EtaEstimator）。
/// </summary>
public static class ProgramPlanner
{
    public const double HeatRate = 3.5, PressMin = 3, StartTemp = 25, HoldCondMax = 15;

    /// <summary>一步的估计分钟（§5，同 HMI 的 StepDuration）与标注。</summary>
    public static (double Min, StepMark Mark) StepMinutes(PcStep s)
    {
        double N(string k, double d) => Fin(s.N(k), d);
        switch (s.Type)
        {
            case StepType.Leak: return (Math.Max(0, N("settle", 5)) + Math.Max(0, N("hold", 30)) + 1, StepMark.None);
            case StepType.Exchange: return ((Math.Max(0, N("lineFlush", 2)) * 9 + Math.Max(0, N("cycles", 3)) * 20) / 60, StepMark.None);
            case StepType.Heat when s.S("mode") == "curve" && s.Segments.Count > 0:
            {
                double m = 0, t = StartTemp;   // 曲线总时长从 25 ℃ 起算（同 HMI 的 StepSpecs.CurveMinutes）
                foreach (var g in s.Segments) { m += g.Rate > 0 ? Math.Abs(g.Temp - t) / g.Rate : 0; t = g.Temp; m += Math.Max(0, g.Hold); }
                return (m, StepMark.None);
            }
            case StepType.Heat:
            {
                double rate = N("rate", 0);
                return (Math.Abs(N("temp", StartTemp) - StartTemp) / (rate > 0 ? rate : HeatRate) + Math.Max(0, N("wait", 0)), StepMark.None);
            }
            case StepType.Press: return (PressMin, StepMark.None);
            case StepType.Hold when s.S("until", "time") == "time": return (Math.Max(0, N("time", 0)), StepMark.None);
            case StepType.Hold: return (Math.Min(Math.Max(0, N("maxWait", 60)), HoldCondMax), StepMark.About);
            case StepType.React: return (Math.Max(0, N("time", 0)), StepMark.UpTo);
            case StepType.Vent: return (1 + Math.Max(0, N("flush", 3)) * 20 / 60, StepMark.None);
            // 旧格式（读进来时已经转换，这里只为不出错）
            case StepType.Purge: return (N("cycles", 0) * (N("fill", 0) + N("vent", 0)) / 60, StepMark.None);
            case StepType.Wait: return (Math.Max(0, N("time", 0)), StepMark.None);
            default: return (0, StepMark.None);
        }
    }

    public static ProgramPlanResult Plan(IReadOnlyList<PcStep> steps)
    {
        double T = StartTemp, P = 0, m = 0;
        var pts = new List<PlanPoint> { new(0, StartTemp, 0) };
        var bounds = new List<PlanBound>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            double m0 = m;
            var (dm, mark) = StepMinutes(s);
            switch (s.Type)
            {
                case StepType.Heat when s.S("mode") == "curve" && s.Segments.Count > 0:
                    double prev = StartTemp;   // 计时同 §5（从 25 ℃ 起算），曲线的温度照实画
                    foreach (var g in s.Segments)
                    {
                        m += g.Rate > 0 ? Math.Abs(g.Temp - prev) / g.Rate : 0;
                        prev = g.Temp;
                        T = g.Temp;
                        pts.Add(new(m, T, P));
                        m += Math.Max(0, g.Hold);
                        pts.Add(new(m, T, P));
                    }
                    break;
                case StepType.Heat:
                {
                    double wait = Math.Max(0, Fin(s.N("wait"), 0));
                    T = Fin(s.N("temp"), T);
                    pts.Add(new(m + dm - wait, T, P));
                    m += dm;
                    pts.Add(new(m, T, P));
                    break;
                }
                case StepType.Leak:
                {
                    double pres = Fin(s.N("pres"), P);
                    pts.Add(new(m + Math.Min(1, dm), T, pres));
                    m += dm;
                    pts.Add(new(m, T, pres));
                    P = s.S("after", "vent") == "vent" ? 0 : pres;
                    if (P == 0) pts.Add(new(m, T, P));
                    break;
                }
                case StepType.Exchange:
                    m += dm;
                    if (Fin(s.N("cycles"), 0) > 0) P = Math.Max(0, Fin(s.N("release"), 0));
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Press:
                    m += dm;
                    P = Math.Max(P, Fin(s.N("pres"), P));   // 只能升、不能降
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Hold when s.S("until", "time") == "tempBelow":
                    m += dm;
                    T = Math.Min(T, Fin(s.N("temp"), T));
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Hold when s.S("until", "time") == "pressBelow":
                    m += dm;
                    P = Math.Min(P, Fin(s.N("pres"), P));
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Vent:
                    m += dm;
                    P = Math.Max(0, Fin(s.N("to"), 0));
                    pts.Add(new(m, T, P));
                    break;
                case StepType.Stir:
                case StepType.End:
                    break;
                default:
                    m += dm;
                    pts.Add(new(m, T, P));
                    break;
            }
            bounds.Add(new PlanBound(m0, m, i, mark));
        }
        return new ProgramPlanResult(pts, bounds, m);
    }

    private static double Fin(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    /// <summary>一步的时长文字（步骤列表的右列）：'45 min' / 按条件 '约 15 min' / 反应 '≤ 8 h 00 min' / 0 = '立即'。</summary>
    public static string Dur(PlanBound b)
    {
        double d = b.M1 - b.M0;
        if (b.Mark == StepMark.None && d < 0.5) return "立即";
        return (b.Mark switch { StepMark.About => "约 ", StepMark.UpTo => "≤ ", _ => "" }) + Hm(d);
    }

    /// <summary>样稿 hm：'2 h 01 min' / '45 min'（四舍五入到分钟）。</summary>
    public static string Hm(double minutes)
    {
        long m = (long)Math.Round(double.IsFinite(minutes) ? minutes : 0, MidpointRounding.AwayFromZero);
        return m >= 60 ? (m / 60).ToString(CultureInfo.InvariantCulture) + " h " + (m % 60).ToString("00", CultureInfo.InvariantCulture) + " min"
            : m.ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>
    /// 步骤列表一行的摘要（样稿 stepDisp 的 rows，两项）：气密「试验压力 3.00 MPa」「保压 30 min」；置换「惰性气体 × 3」「0.50 → 0.02 MPa」；
    /// 搅拌「转速 400 rpm」；升温「目标温度 80.0 °C」「平衡 15 min」；充压「目标压力 2.00 MPa」「气体 A · 稳压」；保持「时间 10 min」/「温度 ≤ 40 °C」「最长 60 min」；
    /// 反应「最长 8 h 00 min」「吸气停止」；放空「放到 0.02 MPa」「惰性置换 × 3」；结束「关加热 · 阀全关」。压力按设置的单位。
    /// </summary>
    public static IReadOnlyList<(string K, string V)> Summary(PcStep s, ProgramSchema schema)
    {
        string L(string key) => schema.Field(s.Type, key) is { } f ? f.LabelOf(s.S(key)) : s.S(key);
        string N1(double v) => JsNumber.ToFixed(v, 1);
        string N0(double v) => double.IsFinite(v) ? JsNumber.ToString(Math.Round(v, 1)) : Fmt.Dash;
        string Pu(string key) => PressureDisplay.InputU(s.N(key));
        string Pv(string key) => PressureDisplay.Input(s.N(key));
        string Hold() => s.S("until", "time") switch
        {
            "tempAbove" => "温度 ≥ " + N0(s.N("temp")) + " °C",
            "tempBelow" => "温度 ≤ " + N0(s.N("temp")) + " °C",
            "pressAbove" => "压力 ≥ " + Pu("pres"),
            "pressBelow" => "压力 ≤ " + Pu("pres"),
            _ => N0(s.N("time")) + " min",
        };
        string ReactEnd()
        {
            var e = new List<string>(2);
            if (s.S("stallOn", "on") == "on") e.Add("吸气停止");
            if (s.S("mmolOn", "off") == "on") e.Add("吸气量 " + JsNumber.ToFixed(s.N("mmol", 5), 2) + " mmol");
            return e.Count > 0 ? string.Join(" / ", e) : "最长时间";
        }
        return s.Type switch
        {
            StepType.Leak => new[] { ("试验压力", Pu("pres")), ("保压", N0(s.N("hold")) + " min") },
            StepType.Exchange when s.N("cycles", 3) <= 0 => new[] { ("气源", L("gas") + " · 只吹总管"), ("吹总管", N0(s.N("lineFlush")) + " 次") },
            StepType.Exchange => new[] { ("气源", L("gas") + " × " + N0(s.N("cycles"))), ("充 / 放", Pv("fill") + " → " + Pu("release")) },
            StepType.Stir => new[] { ("转速", N0(s.N("rpm")) + " rpm"), ("之后", "一直保持") },
            StepType.Heat when s.S("mode") == "curve" => new[] { ("曲线升温", s.Segments.Count + " 段"), ("终温", s.Segments.Count > 0 ? N1(s.Segments[^1].Temp) + " °C" : Fmt.Dash) },
            StepType.Heat => new[] { ("目标温度", N1(s.N("temp")) + " °C"), ("平衡", N0(s.N("wait")) + " min") },
            StepType.Press => new[] { ("目标压力", Pu("pres")), ("气源", L("gas") + (s.S("regulate", "on") == "on" ? " · 稳压" : "")) },
            StepType.Hold when s.S("until", "time") == "time" => new[] { ("时间", Hold()), ("之后", "继续") },
            StepType.Hold => new[] { ("条件", Hold()), ("最长", N0(s.N("maxWait")) + " min") },
            StepType.React => new[] { ("最长", Hm(Fin(s.N("time"), 0))), ("结束于", ReactEnd()) },
            StepType.Vent => new[] { ("放到", Pu("to")), ("之后", s.N("flush", 3) > 0 ? "惰性置换 × " + N0(s.N("flush")) : "不置换") },
            StepType.End => new[] { ("收尾", "关加热 · 阀全关"), ("", "") },
            _ => Array.Empty<(string, string)>(),
        };
    }
}

/// <summary>一条待改项（样稿 progIssues）：第几步、哪个字段、文字。</summary>
public sealed record ProgramIssue(int I, string Key, string Msg);

/// <summary>
/// 程序的校验（样稿 progIssues，范围取设备的 schema）：数值超出范围（含没填；只看显示着的字段，visibleWhen）、选了 HMI 说现在不可用的选项、
/// 曲线升温没有段、置换的「放到」不低于「充到」、反应的最短时间超过最长时间、旧格式的步骤，以及 <see cref="ProgramChecks"/> 的「拒绝」（结束不在最后、先充压后升温超上限）。
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
            if (s.Type.IsLegacy()) { out_.Add(new ProgramIssue(i, "", Step(i, s) + "：旧步骤「" + s.Type.Zh() + "」· 请换成新步骤")); continue; }
            if (spec is null) { out_.Add(new ProgramIssue(i, "", Step(i, s) + "：设备不支持这种步骤")); continue; }
            foreach (var f in spec.Fields)
            {
                if (!f.Visible(s)) continue;
                if (f.IsSelect)
                {
                    var code = s.S(f.Key, f.Options[0].Code);
                    if (f.Options.FirstOrDefault(o => o.Code == code) is { Available: false } bad)
                        out_.Add(new ProgramIssue(i, f.Key, Step(i, s) + "：" + f.Label + "「" + bad.Label + "」" + (bad.Reason ?? "设备不支持")));
                    continue;
                }
                double v = s.N(f.Key);
                if (f.Bad(v))
                    out_.Add(new ProgramIssue(i, f.Key, Step(i, s) + "：" + f.Label + " " + (double.IsFinite(v) ? f.Num(v) + " " + f.ShowUnit : "未填") + " 超出 " + modelLabel + " 范围 " + f.RangeText + " " + f.ShowUnit));
            }
            // 字段之间的范围（STEPS-SPEC §1：放到 0 – 充到 − 1 psi；最短时间 0 – 最长时间）
            if (s.Type == StepType.Exchange && double.IsFinite(s.N("fill")) && double.IsFinite(s.N("release"))
                && Units.MpaToPsi(s.N("release")) > Units.MpaToPsi(s.N("fill")) - 1 + 1e-6)
                out_.Add(new ProgramIssue(i, "release", Step(i, s) + "：放到 " + PressureDisplay.InputU(s.N("release")) + " 要比充到 " + PressureDisplay.InputU(s.N("fill")) + " 低（至少 1 psi）"));
            if (s.Type == StepType.React && double.IsFinite(s.N("minTime")) && double.IsFinite(s.N("time")) && s.N("minTime") > s.N("time") + 1e-9)
                out_.Add(new ProgramIssue(i, "minTime", Step(i, s) + "：最短时间 " + JsNumber.ToString(s.N("minTime")) + " min 超过最长时间 " + JsNumber.ToString(s.N("time")) + " min"));
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
        foreach (var c in ProgramChecks.Of(steps, schema))
            if (c.Level == CheckLevel.Reject) out_.Add(new ProgramIssue(c.I, c.Code, c.Msg));
        return out_;
    }

    private static string Step(int i, PcStep s) => "第 " + (i + 1).ToString("00", CultureInfo.InvariantCulture) + " 步 " + s.Name;
}
