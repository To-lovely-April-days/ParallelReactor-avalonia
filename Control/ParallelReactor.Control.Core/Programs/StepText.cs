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
/// 步骤文字（样稿 PR.sim.stepName / stepText / condText 与 ui.stepSections 的移植，逐字相同）。按型号：气路名称、搅拌方式（共用 / 本釜）、
/// 冷却水、压力小数位都取自 <see cref="ModelProfile"/>；压力单位经 <see cref="PressureDisplay"/>（默认 MPa，D8）。
/// 真机程序（HMI 的 ProgramDoc，psi）先用 <see cref="ProgramDocs.ToSteps"/> 转成同一种步骤，再用这里的函数显示
/// （沿用旧 PlanEditorVm.BuildSections / DurationOf 的参数与默认值）。
/// </summary>
public static class StepText
{
    /// <summary>步骤类型的代码（样稿 step.type）：'heat' …</summary>
    public static string Code(StepKind kind) => kind switch
    {
        StepKind.Heat => "heat",
        StepKind.Press => "press",
        StepKind.React => "react",
        StepKind.Cool => "cool",
        StepKind.End => "end",
        StepKind.Purge => "purge",
        StepKind.Wait => "wait",
        StepKind.Stir => "stir",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>代码 → 类型（'heat' → Heat）；不认识返回 false。</summary>
    public static bool TryParse(string? code, out StepKind kind)
    {
        foreach (StepKind k in (StepKind[])Enum.GetValues(typeof(StepKind)))
            if (Code(k) == code) { kind = k; return true; }
        kind = StepKind.Heat;
        return false;
    }

    /// <summary>步骤类型的中文名（样稿 PR.STEP[type].zh）：吹扫 / 升温 / 充压 / 搅拌 / 等待 / 反应 / 降温 / 结束。</summary>
    public static string KindName(StepKind kind) => kind switch
    {
        StepKind.Purge => "吹扫",
        StepKind.Heat => "升温",
        StepKind.Press => "充压",
        StepKind.Stir => "搅拌",
        StepKind.Wait => "等待",
        StepKind.React => "反应",
        StepKind.Cool => "降温",
        StepKind.End => "结束",
        _ => Code(kind),
    };

    /// <summary>步骤类型的图标名（样稿 PR.STEP[type].icon，App 的 IconView 用）：wind / thermo / gauge / fan / clock / flask / snow / flag。</summary>
    public static string Icon(StepKind kind) => kind switch
    {
        StepKind.Purge => "wind",
        StepKind.Heat => "thermo",
        StepKind.Press => "gauge",
        StepKind.Stir => "fan",
        StepKind.Wait => "clock",
        StepKind.React => "flask",
        StepKind.Cool => "snow",
        StepKind.End => "flag",
        _ => "program",
    };

    /// <summary>步骤名（样稿 PR.sim.stepName）：null → '—'，结束步骤 '结束'，其余按类型。</summary>
    public static string Name(ProgramStep? step) => step is null ? Fmt.Dash : step.Type == StepKind.End ? "结束" : KindName(step.Type);

    /// <summary>
    /// 步骤摘要（样稿 PR.sim.stepText）：'120 ℃' / '降至 40 ℃' / '0.350 MPa · 气体 A' / '惰性气体 × 3' / '72 min' / '泄压 · 降温' / '保持密封'。
    /// 温度按 JS 的 String(number)；搅拌步骤与其它类型为 '—'。
    /// </summary>
    public static string Summary(ModelProfile? model, ProgramStep? step)
    {
        if (step is null) return Fmt.Dash;
        return step.Type switch
        {
            StepKind.Heat => Num(step.Sp) + " ℃",
            StepKind.Cool => "降至 " + Num(step.Sp) + " ℃",
            StepKind.Press => PressureDisplay.WithUnit(model, step.Target) + " · " + GasName(model, step.Gas),
            StepKind.Purge => GasName(model, step.Gas) + " × " + (step.Cycles is int c && c != 0 ? c : 1),
            StepKind.React or StepKind.Wait => Fmt.Minutes(step.Minutes),
            StepKind.End => step.Vent == true ? "泄压 · 降温" : "保持密封",
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
        var e = step?.End;
        if (e is null) return new StepCond("结束条件", "立即");
        return e.Kind switch
        {
            EndKind.Time => new StepCond("步骤时间", "> " + JsNumber.ToString(e.V) + " min"),
            EndKind.Temp => new StepCond("结束条件", "温度 " + e.Op + " " + JsNumber.ToString(e.V) + " ℃"),
            EndKind.Press => new StepCond("结束条件", "压力 " + e.Op + " " + PressValue(model, e.V) + " " + PressureDisplay.Unit),
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
        else if (step.Type == StepKind.Cool) list.Add(new StepSection("加热器", model.HasCooling ? "关闭 · 冷却水降温" : "关闭 · 自然降温", "降至", T(step.Sp)));
        else if (step.Type == StepKind.End) list.Add(new StepSection("加热器", "关闭 · 降温", "降至", T(step.CoolTo)));
        else if (step.Sp != null) list.Add(new StepSection("加热器", "保持", "设定值", T(step.Sp)));
        else list.Add(new StepSection("加热器", "不加热", "设定值", Fmt.Dash));

        // 进气阀 / 排空阀
        string gas = string.IsNullOrEmpty(step.Gas) ? "" : model.GasName(step.Gas);
        if (step.Type == StepKind.Press)
            list.Add(new StepSection(sv, gas + (step.Regulate switch { true => " · 稳压保持", false => " · 目标后保持", null => " · 到压后保持" }), "目标压力", P(model, step.Target)));
        else if (step.Type == StepKind.React) list.Add(new StepSection(sv, "补压保持 · " + gas, "压力", P(model, step.Target)));
        else if (step.Type == StepKind.Purge) list.Add(new StepSection(sv, gas + " · 置换 ×" + (step.Cycles is int c && c != 0 ? c : 1), "充至", P(model, step.Target)));
        else if (step.Type == StepKind.End && step.Vent == true)
            list.Add(new StepSection((model.Vent is { } v ? v.Name.Split(' ')[0] : "排空") + "阀", "打开 · 泄压", "泄压至", P(model, 0)));
        else list.Add(new StepSection(sv, "关闭", "压力", Fmt.Dash));

        // 搅拌
        list.Add(new StepSection(model.StirShort, model.StirMode == StirMode.Shared ? "共用转速" : "本釜转速", "设定值",
            step.Rpm is double r ? JsNumber.ToString(r) + " rpm" : Fmt.Dash, model.StirLabel));
        return list;
    }

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
