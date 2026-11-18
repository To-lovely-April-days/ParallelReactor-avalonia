using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>矩阵里的一路（样稿 d.chs 的一项）：停用（不能参加）、忙（运行中 / 报警 / 急停：结束后才能下发）、预计结束时刻（忙时）、设备上的溶液体积。</summary>
public sealed record MxChannel(int N, bool Off, bool Busy, DateTime? EtaAt = null, double? VolumeMl = null);

/// <summary>
/// 检查矩阵要的设备情况：别名、型号（显示名 / 键）、本机有没有控制权、各路、schema（范围）、溶液体积上限 mL。
/// </summary>
public sealed record MxContext(string Alias, string ModelLabel, string ModelKey, bool CanControl, IReadOnlyList<MxChannel> Channels, ProgramSchema Schema, double VolMax);

/// <summary>一条检查结果（样稿 mxIssues 的 lv）：No = 要先改（琥珀 ⚠，「下发并运行」禁用）；Wait = 等一等（灰 ⏱：通道在跑、没有控制权）。</summary>
public enum MxLevel { No, Wait }

public sealed record MxIssue(MxLevel Level, string Msg);

/// <summary>一路的计划：预计曲线（升压排队已加进去）、总时长、排队等的分钟。</summary>
public sealed record MxRoutePlan(IReadOnlyList<PlanPoint> Points, double Total, double Queue);

/// <summary>
/// 条件矩阵（融合版 F6，样稿「程序页 · 条件矩阵」MX + mxBuild / mxSteps / mxPlan / mxIssues / mxAct）：
/// 同一套步骤（模板），每一路的升温目标、升压目标、反应时长各填各的（<see cref="PerKeys"/>）；硬件共用的参数（搅拌 = 一台电机，吹扫 / 反应气 = 共用总管）
/// 和其余参数只填一次（<see cref="SharedKeys"/>，各路相同便于比较）。每路另有样品名（只存在本机）和必填的溶液体积（1 – 上限 mL，0.1 mL）。
/// 下发时按每一路生成一个程序（HMI 一个通道一个程序，<see cref="StepsFor"/>）；升压步骤在共用总管上排队，按每路约 3 min 估算（<see cref="PlanFor"/>）。
/// 纯逻辑（App 的程序页持有一个实例，本机方案文件 experiments.json 存它，见 App 的 ExperimentStore）。
/// </summary>
public sealed class MatrixPlan
{
    /// <summary>每路可以不同的参数（样稿 MXK）。</summary>
    public static IReadOnlyList<string> PerKeys(StepType t) => t switch
    {
        StepType.Heat => new[] { "temp" },
        StepType.Press => new[] { "pres" },
        StepType.React => new[] { "time" },
        _ => Array.Empty<string>(),
    };

    /// <summary>矩阵里显示的参数（样稿 MXF；每路可以不同的那几个也在里面）。</summary>
    public static IReadOnlyList<string> RowKeys(StepType t) => t switch
    {
        StepType.Purge => new[] { "gas", "cycles" },
        StepType.Heat => new[] { "temp", "wait" },
        StepType.Press => new[] { "gas", "pres", "regulate" },
        StepType.Stir => new[] { "rpm", "dur" },
        StepType.Wait => new[] { "mode", "time" },
        StepType.React => new[] { "endBy", "time" },
        StepType.End => new[] { "mode", "cool" },
        _ => Array.Empty<string>(),
    };

    /// <summary>共用参数为什么只填一次（样稿 MXWHY）。<see cref="IsHardwareShared"/> 的用链接图标，其它用说明图标。</summary>
    public static string Why(StepType t, string key) => t switch
    {
        StepType.Stir => "全部釜共用一台电机",
        StepType.Purge => "吹扫共用总管",
        StepType.Press when key == "gas" => "共用总管，一台只接一种反应气",
        _ => "各路相同，便于比较",
    };

    public static bool IsHardwareShared(StepType t, string key) => t is StepType.Stir or StepType.Purge || (t == StepType.Press && key == "gas");

    public static bool IsPer(StepType t, string key) => PerKeys(t).Contains(key);

    /// <summary>升压排队：每路约 3 min（样稿 mxPlan）。</summary>
    public const double QueueMin = 3;

    public MatrixPlan(PcProgram template, int deviceK)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
        DeviceK = deviceK;
        Steps = template.Steps.Select(s => s.Clone()).ToList();
    }

    public PcProgram Template { get; }
    public string TemplateId => Template.Id;
    public int DeviceK { get; }
    /// <summary>步骤（共用参数就改在这里）。</summary>
    public List<PcStep> Steps { get; }
    /// <summary>每路的参数：键「步序:参数」（'1:temp'）→ 通道 → 值（NaN = 没填）。</summary>
    public Dictionary<string, Dictionary<int, double>> Per { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, string> Samples { get; } = new();
    /// <summary>溶液体积 mL（NaN = 没填）。</summary>
    public Dictionary<int, double> Vol { get; } = new();
    public SortedSet<int> Include { get; } = new();
    public string Name { get; set; } = "";
    public bool Record { get; set; } = true;

    public static string Key(int i, string k) => i.ToString(CultureInfo.InvariantCulture) + ":" + k;

    /// <summary>
    /// 按模板建（样稿 mxBuild）：参加 = 没停用的各路；溶液体积 = 设备上现在的值（没有时 5 mL）；每路参数 = 模板值。
    /// </summary>
    public static MatrixPlan Build(PcProgram template, int deviceK, IReadOnlyList<MxChannel> chs, string name)
    {
        var m = new MatrixPlan(template, deviceK) { Name = name };
        foreach (var c in chs)
        {
            if (!c.Off) m.Include.Add(c.N);
            m.Vol[c.N] = c.VolumeMl is double v && v > 0 ? Math.Round(v, 1) : 5;
        }
        for (int i = 0; i < m.Steps.Count; i++)
            foreach (var k in PerKeys(m.Steps[i].Type))
                m.Per[Key(i, k)] = chs.ToDictionary(c => c.N, _ => m.Steps[i].N(k));
        return m;
    }

    /// <summary>第 n 路某一格的值（没有这一格 = 模板值）。</summary>
    public double PerValue(int i, string k, int n) =>
        Per.TryGetValue(Key(i, k), out var row) && row.TryGetValue(n, out var v) ? v : Steps[i].N(k);

    public void SetPer(int i, string k, int n, double v)
    {
        if (!Per.TryGetValue(Key(i, k), out var row)) Per[Key(i, k)] = row = new Dictionary<int, double>();
        row[n] = v;
    }

    /// <summary>与模板不同（粗体，样稿 chg）。</summary>
    public bool Changed(int i, string k, int n) => i < Template.Steps.Count && Template.Steps[i].Type == Steps[i].Type
        && Math.Abs(PerValue(i, k, n) - Template.Steps[i].N(k)) > 1e-9;

    /// <summary>第 n 路的步骤（样稿 mxSteps：共用参数 + 这一路的值）。</summary>
    public IReadOnlyList<PcStep> StepsFor(int n)
    {
        var list = new List<PcStep>(Steps.Count);
        for (int i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i].Clone();
            foreach (var k in PerKeys(s.Type)) s.Num[k] = PerValue(i, k, n);
            list.Add(s);
        }
        return list;
    }

    /// <summary>第 n 路下发的程序（名字 = 模板名；id = 模板 id，程序库的「运行 N 路」按它数）。</summary>
    public PcProgram ProgramFor(int n)
    {
        var p = new PcProgram { Id = Template.Id, Name = Template.Name, Model = Template.Model, Modified = Template.Modified, By = Template.By };
        p.Steps.AddRange(StepsFor(n));
        return p;
    }

    /// <summary>参加的各路（按通道号）。</summary>
    public IReadOnlyList<int> Included(IReadOnlyList<MxChannel> chs) => chs.Where(c => Include.Contains(c.N)).Select(c => c.N).ToArray();

    /// <summary>
    /// 一路的计划（样稿 mxPlan）：程序估计 + 升压排队（第 j 个参加的通道多等 j × 3 min，从升压那一步开始往后推）。
    /// </summary>
    public MxRoutePlan PlanFor(int n, int j)
    {
        var pl = ProgramPlanner.Plan(StepsFor(n));
        int pi = Steps.FindIndex(s => s.Type == StepType.Press);
        double q = pi < 0 ? 0 : j * QueueMin, at = pi < 0 ? double.PositiveInfinity : pl.Bounds[pi].M0;
        var pts = pl.Points.Select(p => p.M >= at && p.M > 0 ? p with { M = p.M + q } : p).ToArray();
        return new MxRoutePlan(pts, pl.Total + q, q);
    }

    /// <summary>有升压步骤（要排队）。</summary>
    public bool HasPress => Steps.Any(s => s.Type == StepType.Press);

    // ———————————————————— 检查 ————————————————————

    /// <summary>样稿 mxVolBad：没填 → 「未填 · 算不出吸气量」，超范围 →「超出 1–8 mL」；好的为空串。</summary>
    public static string VolBad(double v, double volMax) =>
        !double.IsFinite(v) ? "未填 · 算不出吸气量" : v < 1 || v > volMax ? "超出 1–" + JsNumber.ToString(volMax) + " mL" : "";

    /// <summary>一格超出范围的原因（样稿 mxCellBad）；好的为空串。</summary>
    public static string CellBad(MxContext ctx, StepType t, string k, double v)
    {
        var f = ctx.Schema.Field(t, k);
        if (f is null || f.IsSelect) return "";
        return f.Bad(v) ? "超出 " + ctx.ModelLabel + " 范围 " + f.RangeText + " " + f.ShowUnit : "";
    }

    /// <summary>
    /// 检查（样稿 mxIssues）：模板型号不对、没有参加的通道、每路参数超范围（按值分组：「RV4 4.00 MPa」）、共用参数超范围、
    /// 选了设备不支持的选项、溶液体积没填 / 超范围（按原因分组）；等一等的：参加的通道在跑（最晚几点结束）、本机没有控制权。
    /// </summary>
    public IReadOnlyList<MxIssue> Issues(MxContext ctx)
    {
        var o = new List<MxIssue>();
        var inc = ctx.Channels.Where(c => Include.Contains(c.N)).ToList();
        if (Template.Model != ctx.ModelKey) o.Add(new MxIssue(MxLevel.No, "模板适用 " + ModelLabelOf(Template.Model) + "，" + ctx.Alias + " 是 " + ctx.ModelLabel + " · 先选同型号的模板"));
        if (inc.Count == 0) o.Add(new MxIssue(MxLevel.No, "没有参加的通道 · 在表头勾选"));
        for (int i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i];
            foreach (var k in RowKeys(s.Type))
            {
                var f = ctx.Schema.Field(s.Type, k);
                if (f is null) continue;
                if (f.IsSelect)
                {
                    var code = s.S(k, f.Options[0].Code);
                    if (f.Options.FirstOrDefault(x => x.Code == code) is { Available: false } bad)
                        o.Add(new MxIssue(MxLevel.No, Step(i) + f.Label + "：「" + bad.Label + "」" + (bad.Reason ?? "设备不支持")));
                    continue;
                }
                if (IsPer(s.Type, k))
                {
                    var by = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
                    foreach (var c in inc)
                    {
                        double v = PerValue(i, k, c.N);
                        if (CellBad(ctx, s.Type, k, v).Length == 0) continue;
                        string key = double.IsFinite(v) ? f.Num(v) : "未填";
                        if (!by.TryGetValue(key, out var l)) by[key] = l = new List<int>();
                        l.Add(c.N);
                    }
                    if (by.Count > 0)
                        o.Add(new MxIssue(MxLevel.No, Step(i) + f.Label + "：" + string.Join("；", by.OrderBy(x => x.Value.Min()).Select(x => ChList(x.Value) + " " + x.Key + (x.Key == "未填" ? "" : " " + f.ShowUnit)))
                            + " · " + CellBad(ctx, s.Type, k, double.NaN)));
                }
                else if (CellBad(ctx, s.Type, k, s.N(k)) is { Length: > 0 } why) o.Add(new MxIssue(MxLevel.No, Step(i) + f.Label + "：" + why));
            }
        }
        var vb = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var c in inc)
        {
            string w = VolBad(Vol.TryGetValue(c.N, out var v) ? v : double.NaN, ctx.VolMax);
            if (w.Length == 0) continue;
            if (!vb.TryGetValue(w, out var l)) vb[w] = l = new List<int>();
            l.Add(c.N);
        }
        foreach (var (w, ns) in vb) o.Add(new MxIssue(MxLevel.No, "溶液体积：" + ChList(ns) + " " + w));
        var busy = inc.Where(c => c.Busy).ToList();
        if (busy.Count > 0)
        {
            var e = busy.Where(c => c.EtaAt != null).Select(c => c.EtaAt!.Value).ToList();
            o.Add(new MxIssue(MxLevel.Wait, ChList(busy.Select(c => c.N)) + " 正在运行" + (e.Count > 0 ? "，最晚约 " + Fmt.Hm(e.Max()) + " 结束" : "") + " · 结束后才能下发，可以先保存"));
        }
        if (!ctx.CanControl) o.Add(new MxIssue(MxLevel.Wait, ctx.Alias + "为本地只读 · 在工具条「申请控制」后才能下发"));
        return o;
    }

    private static string Step(int i) => "第 " + (i + 1).ToString("00", CultureInfo.InvariantCulture) + " 步";

    /// <summary>型号键 → 显示名（PR8 → HT-PR8），不认识原样。</summary>
    public static Func<string, string> ModelLabelOf { get; set; } = k => k.StartsWith("PR", StringComparison.Ordinal) ? "HT-" + k : k;

    /// <summary>样稿 mxChList：连续 3 路以上写成 RV1–RV8，其余用顿号。</summary>
    public static string ChList(IEnumerable<int> ns)
    {
        var a = ns.Distinct().OrderBy(x => x).ToArray();
        var parts = new List<string>();
        for (int i = 0; i < a.Length;)
        {
            int j = i;
            while (j + 1 < a.Length && a[j + 1] == a[j] + 1) j++;
            if (j - i >= 2) parts.Add("RV" + a[i] + "–RV" + a[j]);
            else for (int x = i; x <= j; x++) parts.Add("RV" + a[x]);
            i = j + 1;
        }
        return string.Join("、", parts);
    }

    // ———————————————————— 工具（统一 / 等差） ————————————————————

    /// <summary>「统一」：参加的各路都设为第一路的值（样稿 mx-same）。返回提示文字；不到 2 路为 null。</summary>
    public string? Same(int i, string k, IReadOnlyList<int> inc, FieldSpec f)
    {
        if (inc.Count < 2) return null;
        double a = PerValue(i, k, inc[0]);
        foreach (var n in inc) SetPer(i, k, n, a);
        return f.Label + "：参加的 " + inc.Count + " 路都设为 " + f.Num(a) + " " + f.ShowUnit;
    }

    /// <summary>
    /// 「等差」：从第一路到最后一路均匀变化（样稿 mx-ramp，按界面的小数位取整；压力在设置的单位里取整）。首尾相同时不改，提示先改最后一路。
    /// </summary>
    public string? Ramp(int i, string k, IReadOnlyList<int> inc, FieldSpec f)
    {
        if (inc.Count < 2) return null;
        double a = PerValue(i, k, inc[0]), b = PerValue(i, k, inc[^1]);
        if (!double.IsFinite(a) || !double.IsFinite(b)) return "等差填充：先填第一路和最后一路";
        if (Math.Abs(a - b) < 1e-12) return "等差填充：先把最后一路（RV" + inc[^1] + "）改成终点值";
        for (int j = 0; j < inc.Count; j++) SetPer(i, k, inc[j], f.RoundShown(a + (b - a) * j / (inc.Count - 1)));
        return f.Label + "：从 " + f.Num(a) + " 到 " + f.Num(b) + " " + f.ShowUnit + " 等差填充 " + inc.Count + " 路";
    }

    /// <summary>溶液体积统一为第一路的值（样稿 mx-vsame）。</summary>
    public string? VolSame(IReadOnlyList<int> inc)
    {
        if (inc.Count == 0) return null;
        double a = Vol.TryGetValue(inc[0], out var v) ? v : double.NaN;
        foreach (var n in inc) Vol[n] = a;
        return "溶液体积：参加的 " + inc.Count + " 路都设为 " + (double.IsFinite(a) ? JsNumber.ToFixed(a, 1) : "（空）") + " mL";
    }

    /// <summary>升压顺序的说明（样稿 mxSide 的 note2）；不排队时 null。</summary>
    public string? QueueNote(IReadOnlyList<int> order)
    {
        if (order.Count < 2 || !HasPress) return null;
        double q = (order.Count - 1) * QueueMin;
        string seq = order.Count > 4 ? "RV" + order[0] + " → RV" + order[1] + " → … → RV" + order[^1] + "（共 " + order.Count + " 路）"
            : string.Join(" → ", order.Select(n => "RV" + n));
        return "升压步骤按 " + seq + (order.Count > 4 ? "" : " ") + "依次占用共用总管，每路约 3 min；最后一路比第一路晚约 " + JsNumber.ToString(q) + " min 开始反应。对比吸气量时按各路升压完成的时刻对齐。";
    }
}
