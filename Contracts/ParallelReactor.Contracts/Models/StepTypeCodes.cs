using System;

namespace ParallelReactor.Models;

/// <summary>程序步骤类型 ↔ 协议小写码（§6.9：purge / heat / press / stir / wait / react / end）。</summary>
public static class StepTypeCodes
{
    public static readonly StepType[] All = { StepType.Purge, StepType.Heat, StepType.Press, StepType.Stir, StepType.Wait, StepType.React, StepType.End };

    public static string ToCode(this StepType t) => t.ToString().ToLowerInvariant();

    public static bool TryParse(string? code, out StepType type)
        => Enum.TryParse((code ?? "").Trim(), true, out type) && Enum.IsDefined(type);

    public static StepType Parse(string code) => TryParse(code, out var t) ? t : throw new ArgumentException($"未知步骤类型「{code}」");

    /// <summary>中文名（与触屏程序页一致）。</summary>
    public static string Zh(this StepType t) => t switch
    {
        StepType.Purge => "吹扫",
        StepType.Heat => "升温",
        StepType.Press => "充压",
        StepType.Stir => "搅拌",
        StepType.Wait => "等待",
        StepType.React => "反应",
        StepType.End => "结束",
        _ => t.ToString(),
    };
}
