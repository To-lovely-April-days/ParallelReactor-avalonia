using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>sel 字段的一项：协议码 + 中文（同 HMI 的 SchemaOption）；不可用时带原因（选了它算待改项）。</summary>
public sealed record FieldOption(string Code, string Label, bool Available = true, string? Reason = null);

/// <summary>
/// 字段什么时候显示（STEPS-SPEC §6 的 visibleWhen）：另一个字段 <see cref="Key"/> 的值是 <see cref="Codes"/> 之一（sel）；
/// <see cref="Positive"/> = 另一个数值字段大于 0（放空的「置换充到」只在「之后置换」&gt; 0 时有）。
/// </summary>
public sealed record FieldVis(string Key, IReadOnlyList<string> Codes, bool Positive = false)
{
    public bool Test(PcStep s) => Positive ? s.N(Key, 0) > 0 : Codes.Contains(s.S(Key));
}

/// <summary>
/// 程序页的一个参数（融合版 F6）：取自 HMI 的 GET /program-schema（§6.9 FieldSchemaDto）。
/// <see cref="Min"/> / <see cref="Max"/> / <see cref="Default"/> / <see cref="Unit"/> / <see cref="Dp"/> 是<b>电脑端内部</b>的单位 ——
/// 压力字段（v0.8：HMI 的 unit = "psi"；气密试验压力、置换充到 / 放到、充压目标、保持的压力条件、放空放到 / 置换充到）从 psi 换成 MPa（2 位小数；
/// 下限向上、上限向下取到 0.01，保证能输入的值 HMI 一定收），气密的允许压降（psi/h）换成 MPa/h（4 位）；程序库 / 矩阵文件都按它存。
/// 界面上的写法、范围、输入、判断超范围走 <see cref="Num"/> / <see cref="RangeText"/> / <see cref="ShowUnit"/> / <see cref="FromInput"/> / <see cref="Bad"/>：
/// 压力按设置的压力单位（<see cref="PressureDisplay"/>；输入小数位 MPa 2、bar 1、psi 0；压降速率 MPa 4、bar 3、psi 1），范围由 HMI 原来的 psi 限值 <see cref="Psi"/> 换算，其余字段原样。
/// <see cref="Available"/> = HMI 说这个字段现在能不能用；<see cref="Vis"/> = 什么时候显示（null = 一直显示）；<see cref="DefaultCode"/> = 选项字段新建时的码（HMI 的 defaultCode）。
/// </summary>
public sealed record FieldSpec(string Key, string Label, string Unit, double Min, double Max, double Default, int Dp,
    IReadOnlyList<FieldOption> Options, bool Available = true, string? Reason = null, PsiLimits? Psi = null, FieldVis? Vis = null, string? DefaultCode = null)
{
    /// <summary>压降速率的内部单位。</summary>
    public const string MpaPerHour = "MPa/h";

    public bool IsSelect => Options.Count > 0;

    /// <summary>压力字段（内部 MPa，显示 / 输入按设置的压力单位）。</summary>
    public bool IsPressure => !IsSelect && Unit == Units.Mpa;

    /// <summary>压降速率字段（内部 MPa/h，显示按「压力单位/h」）。</summary>
    public bool IsRate => !IsSelect && Unit == MpaPerHour;

    /// <summary>压力或压降速率（按设置的压力单位换算的字段）。</summary>
    public bool IsPress => IsPressure || IsRate;

    /// <summary>这一步现在显示这个字段（<see cref="Vis"/>）。</summary>
    public bool Visible(PcStep s) => Vis is null || Vis.Test(s);

    /// <summary>界面上的单位：压力按设置（'MPa' / 'bar' / 'psi'），压降速率加「/h」，其余同 <see cref="Unit"/>。</summary>
    public string ShowUnit => IsPressure ? PressureDisplay.Unit : IsRate ? PressureDisplay.Unit + "/h" : Unit;

    /// <summary>界面上的小数位：压力 MPa 2、bar 1、psi 0；压降速率 MPa 4、bar 3、psi 1；其余同 <see cref="Dp"/>。</summary>
    public int ShowDp => IsPressure ? PressureDisplay.InputDecimals : IsRate ? RateDecimals(PressureDisplay.Primary) : Dp;

    /// <summary>压降速率的输入小数位（2 psi/h ≈ 0.0138 MPa/h，要 4 位才看得出来）。</summary>
    public static int RateDecimals(PressureUnit u) => u switch { PressureUnit.Psi => 1, PressureUnit.Bar => 3, _ => 4 };

    /// <summary>界面单位的下限（压力：HMI 的 psi 下限换算后向上取到输入小数位；MPa 时 = <see cref="Min"/>）。</summary>
    public double ShowMin => IsPress ? CeilTo(Psi is { } p ? PressureUnits.FromPsi(p.Min, PressureDisplay.Primary) : PressureDisplay.ToUnit(Min), ShowDp) : Min;

    /// <summary>界面单位的上限（压力：向下取到输入小数位；MPa 时 = <see cref="Max"/>）。</summary>
    public double ShowMax => IsPress ? Math.Max(ShowMin, FloorTo(Psi is { } p ? PressureUnits.FromPsi(p.Max, PressureDisplay.Primary) : PressureDisplay.ToUnit(Max), ShowDp)) : Max;

    /// <summary>范围文字（界面单位）：'0.01 – 3.14' / '1 – 455'（psi）/ '0 – 100'（数值字段，不补尾零；sel 为空）。</summary>
    public string RangeText => IsSelect ? "" : JsNumber.ToString(Math.Round(ShowMin, ShowDp)) + " – " + JsNumber.ToString(Math.Round(ShowMax, ShowDp));

    /// <summary>按界面单位和小数位格式化（输入是内部值）：'2.50' / '150' / 压力 '0.35'（MPa）'3.5'（bar）'51'（psi）。</summary>
    public string Num(double v) => IsPressure ? PressureDisplay.Input(v) : IsRate ? (double.IsFinite(v) ? JsNumber.ToFixed(PressureDisplay.ToUnit(v), ShowDp) : Fmt.Dash) : JsNumber.ToFixed(v, Dp);

    /// <summary>内部值 → 界面单位的数（压力换算，其余原样；不取整）。</summary>
    public double Shown(double v) => IsPress ? PressureDisplay.ToUnit(v) : v;

    /// <summary>界面单位的数 → 内部值（压力换回 MPa，其余原样；不取整）。</summary>
    public double FromShown(double shown) => IsPress ? PressureDisplay.FromUnit(shown) : shown;

    /// <summary>
    /// 输入框里的数（界面单位）→ 内部值：先按界面小数位四舍五入（远离 0），压力再换回 MPa（不再取整，psi 50 下发回去仍是 50.0）。NaN 照样 NaN。
    /// </summary>
    public double FromInput(double shown)
    {
        if (!double.IsFinite(shown)) return double.NaN;
        double r = Math.Round(shown, ShowDp, MidpointRounding.AwayFromZero);
        return IsPress ? PressureDisplay.FromUnit(r) : r;
    }

    /// <summary>内部值按界面小数位取整（矩阵等差填充、新步骤的默认值）：压力在界面单位里取整再换回 MPa；MPa 时同 Math.Round(v, Dp)。</summary>
    public double RoundShown(double v)
    {
        if (!double.IsFinite(v)) return v;
        return IsPress ? PressureDisplay.FromUnit(Math.Round(PressureDisplay.ToUnit(v), ShowDp, MidpointRounding.AwayFromZero)) : Math.Round(v, Dp, MidpointRounding.AwayFromZero);
    }

    /// <summary>新步骤的默认值（内部单位）：压力按 HMI 的 psi 默认值在界面单位里取整（psi 时正好 50）；其余 = 夹到范围、按小数位取整的 <see cref="Default"/>。</summary>
    public double NewValue()
    {
        if (IsPress)
        {
            double shown = Psi is { } p ? PressureUnits.FromPsi(p.Default, PressureDisplay.Primary) : PressureDisplay.ToUnit(Default);
            return PressureDisplay.FromUnit(Math.Round(Math.Clamp(shown, ShowMin, ShowMax), ShowDp, MidpointRounding.AwayFromZero));
        }
        return Math.Round(Math.Clamp(Default, Min, Max), Dp);
    }

    /// <summary>值超出范围（NaN / 无穷也算）。压力在界面单位里比（与 <see cref="RangeText"/> 一致）。</summary>
    public bool Bad(double v)
    {
        if (!double.IsFinite(v)) return true;
        if (!IsPress) return v < Min - 1e-9 || v > Max + 1e-9;
        double s = PressureDisplay.ToUnit(v);
        return s < ShowMin - 1e-9 || s > ShowMax + 1e-9;
    }

    /// <summary>码 → 中文（找不到原样）。</summary>
    public string LabelOf(string? code) => Options.FirstOrDefault(o => o.Code == code)?.Label ?? code ?? "";

    private static double CeilTo(double v, int dp) { double k = Math.Pow(10, dp); return Math.Ceiling(v * k - 1e-9) / k; }
    private static double FloorTo(double v, int dp) { double k = Math.Pow(10, dp); return Math.Floor(v * k + 1e-9) / k; }
}

/// <summary>压力字段在 HMI 那边的原始限值（psi）：界面换成 bar / psi 时由它重新换算范围，不经 MPa 的两位小数（1 psi 的下限在 psi 下仍写 1）。</summary>
public sealed record PsiLimits(double Min, double Max, double Default);

/// <summary>一种步骤（HMI StepSchemaDto）：类型、中文名（升温 / 充压 …）、说明、字段（曲线段另见 <see cref="SegmentFields"/>）。</summary>
public sealed record StepSpec(StepType Type, string Label, string Help, IReadOnlyList<FieldSpec> Fields, IReadOnlyList<FieldSpec>? SegmentFields = null)
{
    public FieldSpec? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);

    /// <summary>参数面板最下面的说明（不看具体参数时的写法；按参数变的见 <see cref="NoteFor"/>）。</summary>
    public string? Note => NoteFor(null);

    /// <summary>
    /// 参数面板最下面的说明（硬件事实 + v0.8 的执行语义，STEPS-SPEC §1 / 设计稿三）：置换次数 0 =「只吹总管，釜里没有置换」；曲线升温照样等到位；
    /// 搅拌一台电机、立即进入下一步；结束是固定的最后一步、不降温。
    /// </summary>
    public string? NoteFor(PcStep? s) => Type switch
    {
        StepType.Leak => "固定用惰性气体 · 保压期间不占用气路，别的路可以同时充气",
        StepType.Exchange when s != null && s.N("cycles", 3) <= 0 => "次数 0 = 只吹总管，釜里没有置换（充到 / 放到不起作用）",
        StepType.Exchange => "充到 → 放到算一次 · 充不上去 / 放不下来就报警，停止这一路",
        StepType.Stir => "整台共用一台搅拌电机，所有釜同一转速（釜里的转速）· 设定后立即进入下一步，转速一直保持",
        StepType.Heat when s != null && s.S("mode") == "curve" => "曲线跑完后照样等到位（最后一段温度 ± 到位范围）；曲线自己的保温就是平衡",
        StepType.Heat => "升温速率 0 = 防过冲逼近（尽快）",
        StepType.Press => "上限 = 超压报警的 90 % · 只能升、不能降 · 下发时换算为 psi",
        StepType.Hold => "什么都不动，等条件 · 传感器离线超过 60 s 按超时处理",
        StepType.React => "不动阀、不改温度：温度、压力（稳压）、搅拌由前面的步骤保持",
        StepType.Vent => "放不下来就报警，不要开釜 · 热态（> 60 °C）放空会带出溶剂蒸气",
        StepType.End => "固定的最后一步，不能删、不能挪：关加热、阀全关、释放搅拌、停止吸气计量 · 不降温，要等凉了再放空可在放空前加「保持 · 温度 ≤」",
        StepType.Purge => "旧步骤：只吹扫共用总管，不进反应釜",
        _ => null,
    };
}

/// <summary>
/// 生成本地 schema 用的限值（同 HMI 的 ProgramLimitSettings，压力用 psi）：温度 SP 上限、超温报警、搅拌转速上限（釜转速）、压力 SP 上限、超压报警，
/// 以及气密检查的允许压降默认值（设置 → 泄漏率阈值，psi/h，现在 2）。演示数据 / 还没从 HMI 取到 schema 时用它（规则同 HMI 的 ProgramLimits.FieldRange）。
/// </summary>
public readonly record struct SchemaLimits(double TMaxSp, double OverTemp, double RpmMax, double PMaxSpPsi, double OverPressurePsi, double LeakRatePsiH = 2);

/// <summary>
/// 一台设备的程序 schema（融合版 F6；程序步骤 v0.8，STEPS-SPEC.md）：真机 = GET /program-schema（按设置修订号缓存）；
/// 演示 / 还没取到 = <see cref="Default"/>（同 STEPS-SPEC §1 的表 + 限值规则）。搅拌转速另按设备的釜转速上限封顶（<see cref="Cap"/>）。
/// 只列 v0.8 的九种（<see cref="StepTypeCodes.All"/>）；旧码 purge / wait 不在 schema 里（读进来时已经转换）。
/// </summary>
public sealed class ProgramSchema
{
    private readonly Dictionary<StepType, StepSpec> _steps;

    private ProgramSchema(IEnumerable<StepSpec> steps, string source)
    {
        _steps = steps.ToDictionary(s => s.Type);
        Steps = StepTypeCodes.All.Where(_steps.ContainsKey).Select(t => _steps[t]).ToArray();
        Source = source;
    }

    /// <summary>步骤（编辑器的顺序：气密检查 · 置换 · 搅拌 · 升温 · 充压 · 保持 · 反应 · 放空 · 结束）。</summary>
    public IReadOnlyList<StepSpec> Steps { get; }

    /// <summary>'hmi' = 设备给的；'local' = 本机按限值生成。</summary>
    public string Source { get; }

    public StepSpec? Of(StepType t) => _steps.TryGetValue(t, out var s) ? s : null;

    public FieldSpec? Field(StepType t, string key) => Of(t)?.Field(key);

    /// <summary>非压力字段的小数位（压力 MPa 2、压降速率 MPa/h 4 另算）：吸气量 mmol 2、吸气停止速率 3、升温速率 / 到位范围 / 允许偏差 1，其余 0。</summary>
    public static int DpOf(StepType t, string key) => (t, key) switch
    {
        (StepType.React, "mmol") => 2,
        (StepType.React, "stallRate") => 3,
        (StepType.Heat, "rate" or "band") => 1,
        (StepType.Press, "tol") => 1,
        _ => 0,
    };

    /// <summary>什么时候显示（STEPS-SPEC §6 的 visibleWhen；HMI 的 schema 没带时也按这张表）。</summary>
    public static FieldVis? VisOf(StepType t, string key)
    {
        static FieldVis V(string k, params string[] codes) => new(k, codes);
        return (t, key) switch
        {
            (StepType.Heat, "temp" or "rate" or "wait") => V("mode", "fixed"),
            (StepType.Press, "refill") => V("regulate", "on"),
            (StepType.Hold, "time") => V("until", "time"),
            (StepType.Hold, "temp") => V("until", "tempAbove", "tempBelow"),
            (StepType.Hold, "pres") => V("until", "pressAbove", "pressBelow"),
            (StepType.Hold, "maxWait" or "onTimeout") => V("until", "tempAbove", "tempBelow", "pressAbove", "pressBelow"),
            (StepType.React, "mmol") => V("mmolOn", "on"),
            (StepType.React, "stallRate" or "stallMin") => V("stallOn", "on"),
            _ => null,
        };
    }

    /// <summary>HMI 的 schema → 电脑端（压力 psi → MPa，压降 psi/h → MPa/h；旧码的步骤跳过）。</summary>
    public static ProgramSchema FromDto(ProgramSchemaDto dto)
    {
        if (dto is null) throw new ArgumentNullException(nameof(dto));
        var list = new List<StepSpec>();
        foreach (var s in dto.Steps)
        {
            if (!StepTypeCodes.TryParse(s.Type, out var t) || t.IsLegacy()) continue;
            var fields = new List<FieldSpec>();
            IReadOnlyList<FieldSpec>? seg = null;
            foreach (var f in s.Fields)
            {
                if (f.Kind == "array")
                {
                    seg = (f.ItemFields ?? Array.Empty<FieldSchemaDto>()).Select(x => Num(t, x, segment: true)).ToArray();
                    continue;
                }
                fields.Add(f.Kind == "sel"
                    ? new FieldSpec(f.Key, f.Label, "", 0, 0, 0, 0, (f.Options ?? Array.Empty<SchemaOption>()).Select(o => new FieldOption(o.Code, o.Label, o.Available, o.Reason)).ToArray(), f.Available, f.Reason,
                        Vis: VisFrom(t, f), DefaultCode: f.DefaultCode)
                    : Num(t, f, segment: false));
            }
            list.Add(new StepSpec(t, s.Label, s.Help, fields, seg));
        }
        return new ProgramSchema(list, "hmi");
    }

    /// <summary>
    /// 一个数值字段。压力（unit = psi；旧 HMI 没写单位的 press.pres 也算）→ MPa、2 位；压降速率（psi/h）→ MPa/h、4 位；原始 psi 限值留在 <see cref="FieldSpec.Psi"/>。
    /// </summary>
    private static FieldSpec Num(StepType t, FieldSchemaDto f, bool segment)
    {
        int dp = segment ? (f.Key == "rate" ? 1 : 0) : DpOf(t, f.Key);
        double lo = f.Min ?? f.DeclaredMin ?? 0, hi = f.Max ?? f.DeclaredMax ?? 0, def = f.Default ?? lo;
        string unit = f.Unit ?? "";
        PsiLimits? psi = null;
        bool pressure = !segment && (unit == Units.Psi || (t == StepType.Press && f.Key == "pres"));
        bool rate = !segment && unit == "psi/h";
        if (pressure || rate)
        {
            dp = pressure ? 2 : 4;
            psi = new PsiLimits(lo, Math.Max(lo, hi), def);
            (lo, hi, def) = (CeilTo(Units.PsiToMpa(lo), dp), FloorTo(Units.PsiToMpa(hi), dp), Math.Round(Units.PsiToMpa(def), dp));
            unit = pressure ? Units.Mpa : FieldSpec.MpaPerHour;
        }
        return new FieldSpec(f.Key, f.Label, unit, lo, Math.Max(lo, hi), def, dp, Array.Empty<FieldOption>(), f.Available, f.Reason, psi, segment ? null : VisFrom(t, f));
    }

    /// <summary>显示条件：v0.8 的 HMI 在 schema 里给 visibleWhen（{ key, in }）就用它，没给（较早的 HMI / 本机生成）按 <see cref="VisOf"/> 的表。</summary>
    private static FieldVis? VisFrom(StepType t, FieldSchemaDto f) =>
        f.VisibleWhen is { Key.Length: > 0 } w ? new FieldVis(w.Key, w.In ?? Array.Empty<string>()) : VisOf(t, f.Key);

    private static double CeilTo(double v, int dp) { double k = Math.Pow(10, dp); return Math.Ceiling(v * k - 1e-9) / k; }
    private static double FloorTo(double v, int dp) { double k = Math.Pow(10, dp); return Math.Floor(v * k + 1e-9) / k; }

    /// <summary>HMI StepDef 声明的升压目标上限 psi（34 bar）。</summary>
    public const double PressDeclaredMaxPsi = 493.1;

    /// <summary>
    /// 本机生成（STEPS-SPEC §1 的九张表 + 「上限」）：温度 ≤ min(TMaxSp, 超温 − 1)；压力 ≤ min(PMaxSp, 超压 × 0.9 − 0.1 psi)（再不超过 HMI 声明的 34 bar）；
    /// 转速 0 – 转速上限（釜转速，默认 450），默认 400；气密的允许压降默认 = 设置的泄漏率阈值。默认值、范围、单位、选项、visibleWhen 都同 HMI。
    /// </summary>
    public static ProgramSchema Default(SchemaLimits l)
    {
        static FieldOption[] O(string key) => ProgramSchemaCodes.Options[key].Select(o => new FieldOption(o.Code, o.Label, o.Available, o.Reason)).ToArray();
        static FieldSchemaDto N(string key, string label, string unit, double def, double lo, double hi) =>
            new(key, label, "num", unit, def, lo, hi, lo, hi, null, true, null, null, null, null);
        double heatMax = Math.Max(35, Math.Min(300, Math.Min(l.TMaxSp, l.OverTemp - 1)));   // 声明上限 300 ℃（同 HMI 的 StepSpecs.TempDeclaredMax）
        double pMax = Math.Min(PressDeclaredMaxPsi, Math.Min(l.PMaxSpPsi, l.OverPressurePsi * 0.9 - 0.1));
        double rpmMax = Math.Max(0, l.RpmMax);
        double P(double lo) => Math.Max(lo, pMax);
        FieldSpec F(StepType t, string key, string label, string unit, double def, double lo, double hi) => Num(t, N(key, label, unit, def, lo, Math.Max(lo, hi)), false);
        FieldSpec S(StepType t, string key, string label) => new(key, label, "", 0, 0, 0, 0, O(t.ToCode() + "." + key), Vis: VisOf(t, key));
        var steps = new List<StepSpec>
        {
            new(StepType.Leak, "气密检查", "用惰性气体把这一路充到试验压力，关阀保压，算压降速率。", new[]
            {
                F(StepType.Leak, "pres", "试验压力", Units.Psi, 435.1, 14.5, P(14.5)),
                F(StepType.Leak, "settle", "稳定时间", "min", 5, 0, 60),
                F(StepType.Leak, "hold", "保压时间", "min", 30, 5, 600),
                F(StepType.Leak, "maxDrop", "允许压降", "psi/h", Math.Clamp(l.LeakRatePsiH, 0.1, 100), 0.1, 100),
                S(StepType.Leak, "onFail", "不通过时"),
                S(StepType.Leak, "after", "之后"),
            }),
            new(StepType.Exchange, "置换", "把釜充到一定压力再放掉，重复 N 次（可先吹总管）。", new[]
            {
                S(StepType.Exchange, "gas", "气体"),
                F(StepType.Exchange, "fill", "充到", Units.Psi, 72.5, 7.3, P(7.3)),
                F(StepType.Exchange, "release", "放到", Units.Psi, 2.9, 0, P(1) - 1),
                F(StepType.Exchange, "cycles", "次数", "次", 3, 0, 20),
                F(StepType.Exchange, "lineFlush", "先吹总管", "次", 2, 0, 10),
                F(StepType.Exchange, "maxSec", "单次最长", "s", 60, 10, 300),
            }),
            new(StepType.Stir, "搅拌", "设定釜转速（0 = 停），立即进入下一步，转速一直保持。", new[]
            {
                F(StepType.Stir, "rpm", "转速", Units.Rpm, Math.Min(400, rpmMax), 0, rpmMax),
            }),
            new(StepType.Heat, "升温", "定值或曲线加热，到达目标 ± 范围后再平衡。", new[]
            {
                S(StepType.Heat, "mode", "方式"),
                F(StepType.Heat, "temp", "目标温度", "°C", 80, 35, heatMax),
                F(StepType.Heat, "rate", "升温速率", "°C/min", 0, 0, 20),
                F(StepType.Heat, "band", "到位范围", "°C", 2, 0.5, 10),
                F(StepType.Heat, "wait", "到位后平衡", "min", 15, 0, 120),
                F(StepType.Heat, "maxWait", "最长等待", "min", 120, 10, 600),
                S(StepType.Heat, "onTimeout", "超时"),
            }, ProgramSchemaCodes.SegmentFields.Select(sf => Num(StepType.Heat, N(sf.Key, sf.Label, sf.Unit, 0, sf.Min, sf.Key == "temp" ? Math.Min(sf.Max, heatMax) : sf.Max), true)).ToArray()),
            new(StepType.Press, "充压", "用所选气体充到目标压力，可选之后自动补气（稳压）。", new[]
            {
                S(StepType.Press, "gas", "气体"),
                F(StepType.Press, "pres", "目标压力", Units.Psi, 290.1, 7.3, P(7.3)),
                F(StepType.Press, "tol", "允许偏差", "%", 2, 0.5, 10),
                S(StepType.Press, "regulate", "之后稳压"),
                F(StepType.Press, "refill", "补气阈值", "%", 3, 1, 10),
                F(StepType.Press, "maxSec", "最长充气", "s", 180, 30, 600),
            }),
            new(StepType.Hold, "保持", "什么都不动，等时间到或温度 / 压力达到目标。", new[]
            {
                S(StepType.Hold, "until", "结束条件"),
                F(StepType.Hold, "time", "时间", "min", 10, 0, 1440),
                F(StepType.Hold, "temp", "目标温度", "°C", 40, 0, heatMax),
                F(StepType.Hold, "pres", "目标压力", Units.Psi, 14.5, 0, P(0)),
                F(StepType.Hold, "maxWait", "最长等待", "min", 60, 1, 1440),
                S(StepType.Hold, "onTimeout", "超时"),
            }),
            new(StepType.React, "反应", "保持温度、压力、搅拌，记录吸气量；最长时间 / 吸气量达到 / 吸气停止，先到为准。", new[]
            {
                F(StepType.React, "time", "最长时间", "min", 480, 1, 4320),
                F(StepType.React, "minTime", "最短时间", "min", 30, 0, 4320),
                S(StepType.React, "mmolOn", "吸气量达到"),
                F(StepType.React, "mmol", "吸气量", "mmol", 5, 0.01, 100),
                S(StepType.React, "stallOn", "吸气停止"),
                F(StepType.React, "stallRate", "停止速率", "mmol/min", 0.005, 0.001, 1),
                F(StepType.React, "stallMin", "持续", "min", 30, 5, 240),
            }),
            new(StepType.Vent, "放空", "开 SVn + 排空阀，把这一路放到目标压力以下；可接惰性气体置换。", new[]
            {
                F(StepType.Vent, "to", "放到", Units.Psi, 2.9, 0, Math.Min(72.5, P(0))),
                F(StepType.Vent, "maxSec", "最长时间", "s", 120, 10, 600),
                F(StepType.Vent, "flush", "之后置换", "次", 3, 0, 10),
                F(StepType.Vent, "flushPres", "置换充到", Units.Psi, 43.5, 7.3, P(7.3)),
            }),
            new(StepType.End, "结束", "固定收尾：关加热、阀全关、释放搅拌、停止吸气计量。", Array.Empty<FieldSpec>()),
        };
        return new ProgramSchema(steps, "local");
    }

    /// <summary>搅拌转速再按设备的釜转速上限封顶（HMI 的 schema 已经是釜转速；演示 / 旧 HMI 的 1000 按电机算的上限在这里收紧）。</summary>
    public ProgramSchema Cap(double? rpmMax)
    {
        if (rpmMax is not double cap || !(cap > 0)) return this;
        var stir = Of(StepType.Stir);
        if (stir?.Field("rpm") is not { } f || f.Max <= cap) return this;
        var nf = f with { Max = cap, Default = Math.Min(f.Default, cap) };
        var ns = stir with { Fields = stir.Fields.Select(x => x.Key == "rpm" ? nf : x).ToArray() };
        return new ProgramSchema(Steps.Select(s => s.Type == StepType.Stir ? ns : s), Source);
    }

    /// <summary>调试 / 测试用：'heat.temp 35–200 °C'。</summary>
    public override string ToString() => string.Join("; ", Steps.SelectMany(s => s.Fields.Where(f => !f.IsSelect).Select(f =>
        s.Type.ToCode() + "." + f.Key + " " + JsNumber.ToFixed(f.Min, f.Dp) + "–" + JsNumber.ToFixed(f.Max, f.Dp) + " " + f.Unit)).ToArray()).ToString(CultureInfo.InvariantCulture);
}
