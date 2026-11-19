using System;

namespace ParallelReactor.Models;

/// <summary>
/// 程序步骤类型 ↔ 协议小写码（§6.9）。v0.8 的九种：leak / exchange / stir / heat / press / hold / react / vent / end（STEPS-SPEC.md）；
/// 旧码 purge / wait 只在输入时认（<see cref="IsLegacy"/>），读进来立即转换。
/// </summary>
public static class StepTypeCodes
{
    /// <summary>新程序可用的步骤（编辑器「添加步骤」的顺序，也是 /program-schema 的顺序）。</summary>
    public static readonly StepType[] All =
    {
        StepType.Leak, StepType.Exchange, StepType.Stir, StepType.Heat, StepType.Press,
        StepType.Hold, StepType.React, StepType.Vent, StepType.End,
    };

    /// <summary>旧格式的步骤类型（只在输入时认，按 STEPS-SPEC §3 转换）。</summary>
    public static bool IsLegacy(this StepType t) => t is StepType.Purge or StepType.Wait;

    public static string ToCode(this StepType t) => t.ToString().ToLowerInvariant();

    public static bool TryParse(string? code, out StepType type)
        => Enum.TryParse((code ?? "").Trim(), true, out type) && Enum.IsDefined(type);

    public static StepType Parse(string code) => TryParse(code, out var t) ? t : throw new ArgumentException($"未知步骤类型「{code}」");

    /// <summary>中文名（触屏程序页、PC 程序页一致）。</summary>
    public static string Zh(this StepType t) => t switch
    {
        StepType.Leak => "气密检查",
        StepType.Exchange => "置换",
        StepType.Stir => "搅拌",
        StepType.Heat => "升温",
        StepType.Press => "充压",
        StepType.Hold => "保持",
        StepType.React => "反应",
        StepType.Vent => "放空",
        StepType.End => "结束",
        StepType.Purge => "吹扫（旧）",
        StepType.Wait => "等待（旧）",
        _ => t.ToString(),
    };
}
