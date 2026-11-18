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
/// 程序页的一个参数（融合版 F6）：取自 HMI 的 GET /program-schema（§6.9 FieldSchemaDto）。
/// <see cref="Min"/> / <see cref="Max"/> / <see cref="Default"/> / <see cref="Unit"/> / <see cref="Dp"/> 是<b>电脑端内部</b>的单位 ——
/// 只有升压的目标压力从 psi 换成 MPa（2 位小数；下限向上、上限向下取到 0.01，保证能输入的值 HMI 一定收），程序库 / 矩阵文件都按它存。
/// 界面上的写法、范围、输入、判断超范围走 <see cref="Num"/> / <see cref="RangeText"/> / <see cref="ShowUnit"/> / <see cref="FromInput"/> / <see cref="Bad"/>：
/// 压力按设置的压力单位（<see cref="PressureDisplay"/>；输入小数位 MPa 2、bar 1、psi 0），范围由 HMI 原来的 psi 限值 <see cref="Psi"/> 换算，其余字段原样。
/// <see cref="Available"/> = HMI 说这个字段现在能不能用。
/// </summary>
public sealed record FieldSpec(string Key, string Label, string Unit, double Min, double Max, double Default, int Dp,
    IReadOnlyList<FieldOption> Options, bool Available = true, string? Reason = null, PsiLimits? Psi = null)
{
    public bool IsSelect => Options.Count > 0;

    /// <summary>压力字段（内部 MPa，显示 / 输入按设置的压力单位）。</summary>
    public bool IsPressure => !IsSelect && Unit == Units.Mpa;

    /// <summary>界面上的单位：压力按设置（'MPa' / 'bar' / 'psi'），其余同 <see cref="Unit"/>。</summary>
    public string ShowUnit => IsPressure ? PressureDisplay.Unit : Unit;

    /// <summary>界面上的小数位：压力 MPa 2、bar 1、psi 0；其余同 <see cref="Dp"/>。</summary>
    public int ShowDp => IsPressure ? PressureDisplay.InputDecimals : Dp;

    /// <summary>界面单位的下限（压力：HMI 的 psi 下限换算后向上取到输入小数位；MPa 时 = <see cref="Min"/>）。</summary>
    public double ShowMin => IsPressure ? CeilTo(Psi is { } p ? PressureUnits.FromPsi(p.Min, PressureDisplay.Primary) : PressureDisplay.ToUnit(Min), ShowDp) : Min;

    /// <summary>界面单位的上限（压力：向下取到输入小数位；MPa 时 = <see cref="Max"/>）。</summary>
    public double ShowMax => IsPressure ? Math.Max(ShowMin, FloorTo(Psi is { } p ? PressureUnits.FromPsi(p.Max, PressureDisplay.Primary) : PressureDisplay.ToUnit(Max), ShowDp)) : Max;

    /// <summary>范围文字（界面单位）：'0.01 – 3.14' / '1 – 455'（psi）/ '0 – 100'（数值字段，不补尾零；sel 为空）。</summary>
    public string RangeText => IsSelect ? "" : JsNumber.ToString(Math.Round(ShowMin, ShowDp)) + " – " + JsNumber.ToString(Math.Round(ShowMax, ShowDp));

    /// <summary>按界面单位和小数位格式化（输入是内部值）：'2.50' / '150' / 压力 '0.35'（MPa）'3.5'（bar）'51'（psi）。</summary>
    public string Num(double v) => IsPressure ? PressureDisplay.Input(v) : JsNumber.ToFixed(v, Dp);

    /// <summary>内部值 → 界面单位的数（压力换算，其余原样；不取整）。</summary>
    public double Shown(double v) => IsPressure ? PressureDisplay.ToUnit(v) : v;

    /// <summary>界面单位的数 → 内部值（压力换回 MPa，其余原样；不取整）。</summary>
    public double FromShown(double shown) => IsPressure ? PressureDisplay.FromUnit(shown) : shown;

    /// <summary>
    /// 输入框里的数（界面单位）→ 内部值：先按界面小数位四舍五入（远离 0），压力再换回 MPa（不再取整，psi 50 下发回去仍是 50.0）。NaN 照样 NaN。
    /// </summary>
    public double FromInput(double shown)
    {
        if (!double.IsFinite(shown)) return double.NaN;
        double r = Math.Round(shown, ShowDp, MidpointRounding.AwayFromZero);
        return IsPressure ? PressureDisplay.FromUnit(r) : r;
    }

    /// <summary>内部值按界面小数位取整（矩阵等差填充、新步骤的默认值）：压力在界面单位里取整再换回 MPa；MPa 时同 Math.Round(v, Dp)。</summary>
    public double RoundShown(double v)
    {
        if (!double.IsFinite(v)) return v;
        return IsPressure ? PressureDisplay.FromUnit(Math.Round(PressureDisplay.ToUnit(v), ShowDp, MidpointRounding.AwayFromZero)) : Math.Round(v, Dp, MidpointRounding.AwayFromZero);
    }

    /// <summary>新步骤的默认值（内部单位）：压力按 HMI 的 psi 默认值在界面单位里取整（psi 时正好 50）；其余 = 夹到范围、按小数位取整的 <see cref="Default"/>。</summary>
    public double NewValue()
    {
        if (IsPressure)
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
        if (!IsPressure) return v < Min - 1e-9 || v > Max + 1e-9;
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

/// <summary>一种步骤（HMI StepSchemaDto）：类型、中文名（升温 / 升压 …）、说明、字段（曲线段另见 <see cref="SegmentFields"/>）。</summary>
public sealed record StepSpec(StepType Type, string Label, string Help, IReadOnlyList<FieldSpec> Fields, IReadOnlyList<FieldSpec>? SegmentFields = null)
{
    public FieldSpec? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);

    /// <summary>参数面板最下面的说明（硬件事实：搅拌一台电机、吹扫只吹总管、升压上限）。</summary>
    public string? Note => Type switch
    {
        StepType.Purge => "只吹扫共用总管，不进反应釜",
        StepType.Press => "上限 = 超压报警的 90 %；下发时换算为 psi",
        StepType.Stir => "整台共用一台搅拌电机，所有釜同一转速（釜里的转速）",
        _ => null,
    };
}

/// <summary>
/// 生成本地 schema 用的限值（同 HMI 的 ProgramLimitSettings，压力用 psi）：温度 SP 上限、超温报警、搅拌转速上限（釜转速）、压力 SP 上限、超压报警。
/// 演示数据 / 还没从 HMI 取到 schema 时用它（规则同 HMI 的 ProgramLimits.FieldRange）。
/// </summary>
public readonly record struct SchemaLimits(double TMaxSp, double OverTemp, double RpmMax, double PMaxSpPsi, double OverPressurePsi);

/// <summary>
/// 一台设备的程序 schema（融合版 F6）：真机 = GET /program-schema（按设置修订号缓存）；演示 / 还没取到 = <see cref="Default"/>（同 HMI 的 StepDef 表 + 限值规则）。
/// 搅拌转速另按设备的釜转速上限封顶（<see cref="Cap"/>）。
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

    /// <summary>步骤（HMI 的顺序：吹扫 · 升温 · 升压 · 搅拌 · 等待 · 反应 · 结束）。</summary>
    public IReadOnlyList<StepSpec> Steps { get; }

    /// <summary>'hmi' = 设备给的；'local' = 本机按限值生成。</summary>
    public string Source { get; }

    public StepSpec? Of(StepType t) => _steps.TryGetValue(t, out var s) ? s : null;

    public FieldSpec? Field(StepType t, string key) => Of(t)?.Field(key);

    /// <summary>小数位：压力 2、mmol 1，其余 0。</summary>
    public static int DpOf(StepType t, string key) => (t, key) switch
    {
        (StepType.Press, "pres") => 2,
        (StepType.React, "mmol") => 1,
        _ => 0,
    };

    /// <summary>HMI 的 schema → 电脑端（压力 psi → MPa）。</summary>
    public static ProgramSchema FromDto(ProgramSchemaDto dto)
    {
        if (dto is null) throw new ArgumentNullException(nameof(dto));
        var list = new List<StepSpec>();
        foreach (var s in dto.Steps)
        {
            if (!StepTypeCodes.TryParse(s.Type, out var t)) continue;
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
                    ? new FieldSpec(f.Key, f.Label, "", 0, 0, 0, 0, (f.Options ?? Array.Empty<SchemaOption>()).Select(o => new FieldOption(o.Code, o.Label, o.Available, o.Reason)).ToArray(), f.Available, f.Reason)
                    : Num(t, f, segment: false));
            }
            list.Add(new StepSpec(t, s.Label, s.Help, fields, seg));
        }
        return new ProgramSchema(list, "hmi");
    }

    private static FieldSpec Num(StepType t, FieldSchemaDto f, bool segment)
    {
        int dp = segment ? (f.Key == "rate" ? 1 : 0) : DpOf(t, f.Key);
        double lo = f.Min ?? f.DeclaredMin ?? 0, hi = f.Max ?? f.DeclaredMax ?? 0, def = f.Default ?? lo;
        string unit = f.Unit ?? "";
        PsiLimits? psi = null;
        if (!segment && t == StepType.Press && f.Key == "pres")
        {
            psi = new PsiLimits(lo, Math.Max(lo, hi), def);
            (lo, hi, def) = (CeilTo(Units.PsiToMpa(lo), dp), FloorTo(Units.PsiToMpa(hi), dp), Math.Round(Units.PsiToMpa(def), dp));
            unit = Units.Mpa;
        }
        return new FieldSpec(f.Key, f.Label, unit, lo, Math.Max(lo, hi), def, dp, Array.Empty<FieldOption>(), f.Available, f.Reason, psi);
    }

    private static double CeilTo(double v, int dp) { double k = Math.Pow(10, dp); return Math.Ceiling(v * k - 1e-9) / k; }
    private static double FloorTo(double v, int dp) { double k = Math.Pow(10, dp); return Math.Floor(v * k + 1e-9) / k; }

    /// <summary>
    /// 本机生成（同 HMI 的 HmiApiSources.BuildSchema + Models/ProgramStep.cs 的 StepDef 表 + ProgramLimits.FieldRange）：
    /// 升温目标 35 – min(TMaxSp, 超温 − 1)；升压目标 1 psi – min(34 bar, PMaxSp, 超压 × 0.9 − 0.1 psi)；转速 0 – min(2000, 转速上限)；降温至 0 – min(300, TMaxSp)。
    /// </summary>
    /// <summary>HMI StepDef 声明的升压目标上限 psi（34 bar）。</summary>
    public const double PressDeclaredMaxPsi = 493.1;

    public static ProgramSchema Default(SchemaLimits l)
    {
        static FieldOption[] O(string key) => ProgramSchemaCodes.Options[key].Select(o => new FieldOption(o.Code, o.Label, o.Available, o.Reason)).ToArray();
        static FieldSchemaDto N(string key, string label, string unit, double def, double lo, double hi) =>
            new(key, label, "num", unit, def, lo, hi, lo, hi, null, true, null, null, null, null);
        FieldSpec Sel(string key, string label, string opt) => new(key, label, "", 0, 0, 0, 0, O(opt));
        double heatMax = Math.Min(Math.Min(300, l.TMaxSp), l.OverTemp - 1);
        // HMI 的 StepDef 把 press.pres 声明为 0–493.1 psi（= 34 bar，2026-11-18 用户确认的升压目标上限）；实际上限再受 PMaxSp 与超压 × 0.9 约束（同 HMI ProgramLimits.PressMax）
        double pMax = Math.Min(PressDeclaredMaxPsi, Math.Min(l.PMaxSpPsi, l.OverPressurePsi * 0.9 - 0.1));
        var steps = new List<StepSpec>
        {
            new(StepType.Purge, "吹扫", "只吹扫气路总管：8 路 SV 全程关闭，釜内气体不受影响。", new[]
            {
                Sel("gas", "气体", "purge.gas"),
                Num(StepType.Purge, N("cycles", "循环次数", "次", 10, 1, 50), false),
                Num(StepType.Purge, N("fill", "充气时间", "秒", 3, 1, 30), false),
                Num(StepType.Purge, N("vent", "排气时间", "秒", 5, 1, 60), false),
            }),
            new(StepType.Heat, "升温", "先升温至目标（实测到位 ±2℃ 才进下一步）；「等待平衡」为到位后的保温计时。", new[]
            {
                Sel("mode", "升温方式", "heat.mode"),
                Num(StepType.Heat, N("temp", "目标温度", "°C", 150, 35, Math.Max(35, heatMax)), false),
                Num(StepType.Heat, N("wait", "等待平衡", "分钟", 15, 0, 120), false),
            }, ProgramSchemaCodes.SegmentFields.Select(sf => Num(StepType.Heat, N(sf.Key, sf.Label, sf.Unit, 0, sf.Min, sf.Key == "temp" ? Math.Min(sf.Max, heatMax) : sf.Max), true)).ToArray()),
            new(StepType.Press, "升压", "稳压建议：低气体消耗反应（< 0.5 mmol/h）关闭稳压可获更低 rsd。", new[]
            {
                Sel("gas", "反应气", "press.gas"),
                Num(StepType.Press, N("pres", "目标压力", "psi", 50, 1, Math.Max(1, pMax)), false),
                Sel("regulate", "压力稳压", "press.regulate"),
            }),
            new(StepType.Stir, "搅拌", "搅拌为全机共用一台电机。步骤持续「搅拌时长」后自动停止搅拌并进入下一步。", new[]
            {
                Num(StepType.Stir, N("rpm", "转速", "rpm", Math.Min(1000, l.RpmMax), 0, Math.Min(2000, l.RpmMax)), false),
                Num(StepType.Stir, N("dur", "搅拌时长", "分钟", 5, 0, 600), false),
            }),
            new(StepType.Wait, "等待", "用「等待温度/压力到位」可让程序自适应实际耗时，比固定时长更稳健。", new[]
            {
                Sel("mode", "条件", "wait.mode"),
                Num(StepType.Wait, N("time", "最长时间", "分钟", 30, 0, 600), false),
            }),
            new(StepType.React, "反应", "结束条件中选「两者先到」可避免反应完成后还在继续加热消耗气体。", new[]
            {
                Sel("endBy", "结束条件", "react.endBy"),
                Num(StepType.React, N("time", "最长时间", "分钟", 120, 0, 600), false),
                Num(StepType.React, N("mmol", "目标 mmol", "mmol", 5, 0, 100), false),
            }),
            new(StepType.End, "结束", "强烈推荐使用「密封」：高温下吹扫或排空会让溶剂蒸气污染气路 manifold。", new[]
            {
                Sel("mode", "方式", "end.mode"),
                Num(StepType.End, N("cool", "先降温至", "°C", 40, 0, Math.Min(300, l.TMaxSp)), false),
            }),
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
