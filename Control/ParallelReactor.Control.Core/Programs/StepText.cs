using System;
using System.Collections.Generic;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>步骤卡片里的一节：设备名（'加热器' / '进气阀 SV1' / '搅拌电机（全局）'）、方式、标签、数值；Tip = 设备名的完整写法（悬停提示）。</summary>
public sealed record StepSection(string Dev, string Mode, string Label, string Value, string? Tip = null)
{
    /// <summary>悬停提示：Tip，没有时为设备名。</summary>
    public string DevTip => Tip ?? Dev;
}

/// <summary>步骤下方的条件框：'步骤时间' + '> 72 min'，或 '结束条件' + '温度 ≥ 119.5 ℃'。</summary>
public readonly record struct StepCond(string Label, string Value)
{
    public override string ToString() => Label + " " + Value;
}

/// <summary>
/// 步骤文字（样稿 PR.sim.stepName / stepText / condText 与 ui.stepSections 的移植，旧步骤逐字相同；v0.8 的新步骤见 STEPS-SPEC.md）。按型号：气路名称、搅拌方式（共用 / 本釜）、
/// 冷却水、压力小数位都取自 <see cref="ModelProfile"/>；压力单位经 <see cref="PressureDisplay"/>（默认 MPa，D8）。
/// 真机程序（HMI 的 ProgramDoc，psi）先用 <see cref="ProgramDocs.ToSteps"/> 转成同一种步骤，再用这里的函数显示
/// （沿用旧 PlanEditorVm.BuildSections / DurationOf 的参数与默认值）。
/// </summary>
public static class StepText
{
    /// <summary>步骤类型的代码（样稿 step.type / 协议码）：'heat' / 'leak' …</summary>
    public static string Code(StepKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>代码 → 类型（'heat' → Heat）；不认识返回 false。</summary>
    public static bool TryParse(string? code, out StepKind kind)
    {
        foreach (StepKind k in (StepKind[])Enum.GetValues(typeof(StepKind)))
            if (Code(k) == code) { kind = k; return true; }
        kind = StepKind.Heat;
        return false;
    }

    /// <summary>步骤类型的中文名（同 HMI 的 StepTypeCodes.Zh，旧格式不带「（旧）」）：气密检查 / 置换 / 搅拌 / 升温 / 充压 / 保持 / 反应 / 放空 / 结束；吹扫 / 等待。</summary>
    public static string KindName(StepKind kind) => kind switch
    {
        StepKind.Leak => "气密检查",
        StepKind.Exchange => "置换",
        StepKind.Purge => "吹扫",
        StepKind.Heat => "升温",
        StepKind.Press => "充压",
        StepKind.Stir => "搅拌",
        StepKind.Hold => "保持",
        StepKind.Wait => "等待",
        StepKind.React => "反应",
        StepKind.Vent => "放空",
        StepKind.End => "结束",
        _ => Code(kind),
    };

    /// <summary>步骤类型的图标名（App 的 IconView 用）：lock / refresh / wind / thermo / gauge / fan / clock / flask / flag。</summary>
    public static string Icon(StepKind kind) => kind switch
    {
        StepKind.Leak => "lock",
        StepKind.Exchange => "refresh",
        StepKind.Purge or StepKind.Vent => "wind",
        StepKind.Heat => "thermo",
        StepKind.Press => "gauge",
        StepKind.Stir => "fan",
        StepKind.Wait or StepKind.Hold => "clock",
        StepKind.React => "flask",
        StepKind.End => "flag",
        _ => "program",
    };

    /// <summary>结束步骤是旧格式（带「先降温至」/「方式」：显示旧程序的「泄压 · 降温」「保持密封」）；v0.8 的结束没有参数。</summary>
    public static bool LegacyEnd(ProgramStep s) => s.Type == StepKind.End && (s.CoolTo != null || s.Vent != null);

    /// <summary>
    /// 步骤名（样稿 PR.sim.stepName）：null → '—'；步骤自己写了名字（<see cref="ProgramStep.Name"/>：融合版程序库「恒温反应」、HMI 程序的 name）用它；否则按类型。
    /// </summary>
    public static string Name(ProgramStep? step) => step is null ? Fmt.Dash : step.Name is { Length: > 0 } nm ? nm : KindName(step.Type);

    /// <summary>
    /// 步骤摘要（样稿 PR.sim.stepText + v0.8）：'120 ℃' / '0.350 MPa · 气体 A' / '惰性气体 × 3' / '72 min' / '泄压 · 降温' / '保持密封'（旧结束）；
    /// v0.8：气密 '3.00 MPa · 保压 30 min'、置换 '惰性气体 × 3'（次数 0 = '惰性气体 · 只吹总管 × 2'）、搅拌 '400 rpm'、保持 '10 min' / '温度 ≤ 40 ℃'、
    /// 反应 '≤ 8 h 00 min'、放空 '放到 0.020 MPa · 惰性置换 × 3'、结束 '关加热 · 阀全关'。温度按 JS 的 String(number)。
    /// </summary>
    public static string Summary(ModelProfile? model, ProgramStep? step)
    {
        if (step is null) return Fmt.Dash;
        return step.Type switch
        {
            StepKind.Heat => Num(step.Sp) + " ℃",
            StepKind.Press => PressureDisplay.WithUnit(model, step.Target) + " · " + GasName(model, step.Gas),
            StepKind.Purge => GasName(model, step.Gas) + " × " + (step.Cycles is int c && c != 0 ? c : 1),
            StepKind.Leak => PressureDisplay.WithUnit(model, step.Target) + " · 保压 " + Fmt.Minutes(step.Minutes),
            StepKind.Exchange => step.Cycles is int ec && ec > 0 ? GasName(model, step.Gas) + " × " + ec
                : GasName(model, step.Gas) + " · 只吹总管 × " + (step.LineFlush ?? 0),
            StepKind.Stir => step.Rpm is double r ? JsNumber.ToString(r) + " rpm" : Fmt.Dash,
            StepKind.Hold => step.End is { Kind: not EndKind.Time } he ? (he.Kind == EndKind.Temp ? "温度 " + he.Op + " " + JsNumber.ToString(he.V) + " ℃"
                : "压力 " + he.Op + " " + PressureDisplay.WithUnit(model, he.V)) : Fmt.Minutes(step.Minutes),
            StepKind.React => (step.Mark == StepMark.UpTo ? "≤ " : "") + Fmt.Minutes(step.Minutes),
            StepKind.Wait => Fmt.Minutes(step.Minutes),
            StepKind.Vent => "放到 " + PressureDisplay.WithUnit(model, step.Target) + (step.Cycles is int f && f > 0 ? " · 惰性置换 × " + f : ""),
            StepKind.End when LegacyEnd(step) => step.Vent == true ? "泄压 · 降温" : "保持密封",
            StepKind.End => "关加热 · 阀全关",
            _ => Fmt.Dash,
        };
    }

    /// <summary>
    /// 结束条件（样稿 PR.sim.condText）：没有条件 → ('结束条件', '立即')；时间 → ('步骤时间', '> 72 min')；
    /// 温度 → ('结束条件', '温度 ≥ 119.5 ℃')；压力 → ('结束条件', '压力 ≥ 0.350 MPa')。
    /// 给了型号时压力按型号的小数位（HT-PR4 '5.20 MPa'）；不给时同样稿 PR.fmt.mpa 的默认（&lt; 10 用 3 位）。
    /// </summary>
    public static StepCond Cond(ProgramStep? step, ModelProfile? model = null)
    {
        // v0.8：反应 = 最长时间 + 吸气条件；置换按次数结束（没有读数条件）
        if (step is { Type: StepKind.React, Mark: StepMark.UpTo })
            return new StepCond("结束条件", "≤ " + JsNumber.ToString(step.Minutes ?? 0) + " min" + (step.Ends is { Length: > 0 } en ? " · " + en : ""));
        if (step is { Type: StepKind.Exchange })
            return new StepCond("结束条件", step.Cycles is int c && c > 0 ? "置换 " + c + " 次完成" : "吹总管 " + (step.LineFlush ?? 0) + " 次完成");
        var e = step?.End;
        if (e is null) return new StepCond("结束条件", "立即");
        string max = step!.MaxMin is double mx && e.Kind != EndKind.Time ? " · 最长 " + JsNumber.ToString(mx) + " min" : "";
        return e.Kind switch
        {
            EndKind.Time => new StepCond("步骤时间", "> " + JsNumber.ToString(e.V) + " min"),
            EndKind.Temp => new StepCond("结束条件", "温度 " + e.Op + " " + JsNumber.ToString(e.V) + " ℃" + max),
            EndKind.Press => new StepCond("结束条件", "压力 " + e.Op + " " + PressValue(model, e.V) + " " + PressureDisplay.Unit + max),
            _ => new StepCond("结束条件", Fmt.Dash),
        };
    }

    /// <summary>
    /// 步骤卡片的三节（样稿 ui.stepSections）：加热器、进气阀 SV<paramref name="n"/>（结束泄压时为排空阀）、搅拌电机。
    /// <paramref name="n"/> 为 null 时写 '进气阀 SV'（程序库预览，没有具体反应釜）。
    /// </summary>
    public static IReadOnlyList<StepSection> Sections(ModelProfile model, int? n, ProgramStep step)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (step is null) throw new ArgumentNullException(nameof(step));
        string sv = "进气阀 SV" + (n is int k ? k.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        var list = new List<StepSection>(3);

        // 加热器
        if (step.Type == StepKind.Heat) list.Add(new StepSection("加热器", step.HeatCurve ? "曲线升温" : "固定升温", "设定值", T(step.Sp)));
        else if (LegacyEnd(step)) list.Add(new StepSection("加热器", "关闭 · 降温", "降至", T(step.CoolTo)));
        else if (step.Type == StepKind.End) list.Add(new StepSection("加热器", "关闭", "设定值", Fmt.Dash));
        else if (step.Sp != null) list.Add(new StepSection("加热器", "保持", "设定值", T(step.Sp)));
        else list.Add(new StepSection("加热器", "不加热", "设定值", Fmt.Dash));

        // 进气阀 / 排空阀
        string gas = string.IsNullOrEmpty(step.Gas) ? "" : model.GasName(step.Gas);
        if (step.Type == StepKind.Press)
            list.Add(new StepSection(sv, gas + (step.Regulate switch { true => " · 稳压保持", false => " · 目标后保持", null => " · 到压后保持" }), "目标压力", P(model, step.Target)));
        else if (step.Type == StepKind.React) list.Add(new StepSection(sv, "补压保持 · " + gas, "压力", P(model, step.Target)));
        else if (step.Type == StepKind.Purge) list.Add(new StepSection(sv, gas + " · 置换 ×" + (step.Cycles is int c && c != 0 ? c : 1), "充至", P(model, step.Target)));
        else if (step.Type == StepKind.Leak) list.Add(new StepSection(sv, gas + " · 保压检查", "试验压力", P(model, step.Target)));
        else if (step.Type == StepKind.Exchange)
            list.Add(step.Cycles is int ec && ec > 0 ? new StepSection(sv, gas + " · 置换 ×" + ec, "充至", P(model, step.Target)) : new StepSection(sv, gas + " · 只吹总管", "压力", Fmt.Dash));
        else if (step.Type == StepKind.Vent)
            list.Add(new StepSection(VentValve(model), "打开 · 放空" + (step.Cycles is int f && f > 0 ? " · 惰性置换 ×" + f : ""), "放到", P(model, step.Target)));
        else if (step.Type == StepKind.End && step.Vent == true)
            list.Add(new StepSection(VentValve(model), "打开 · 泄压", "泄压至", P(model, 0)));
        else if (step.Type == StepKind.End && !LegacyEnd(step)) list.Add(new StepSection(sv, "全关", "压力", Fmt.Dash));
        else list.Add(new StepSection(sv, "关闭", "压力", Fmt.Dash));

        // 搅拌
        list.Add(new StepSection(model.StirShort, model.StirMode == StirMode.Shared ? "共用转速" : "本釜转速", "设定值",
            step.Rpm is double r ? JsNumber.ToString(r) + " rpm" : Fmt.Dash, model.StirLabel));
        return list;
    }

    /// <summary>排空阀的名字（型号表 Vent 名称的第一个词 + 「阀」：「排空阀」）。</summary>
    private static string VentValve(ModelProfile model) => (model.Vent is { } v ? v.Name.Split(' ')[0] : "排空") + "阀";

    /// <summary>气路显示名：型号表里的名称（'气体 A' / 'H₂'）；没有型号时原样。</summary>
    public static string GasName(ModelProfile? model, string? key) => model is null ? key ?? "" : model.GasName(key);

    /// <summary>压力数值按型号小数位（主单位为 MPa 时）或不给型号时按样稿 PR.fmt.mpa 的默认小数位。</summary>
    private static string PressValue(ModelProfile? model, double v) =>
        model is null && PressureDisplay.Primary == PressureUnit.MPa ? Fmt.Mpa(v) : PressureDisplay.Value(model, v);

    private static string T(double? v) => v is null ? Fmt.Dash : Fmt.Num(v, 1) + " ℃";

    private static string P(ModelProfile model, double? v) => v is null ? Fmt.Dash : PressureDisplay.WithUnit(model, v);

    /// <summary>JS 的 String(number)；null → '—'。</summary>
    private static string Num(double? v) => v is double d ? JsNumber.ToString(d) : Fmt.Dash;
}
