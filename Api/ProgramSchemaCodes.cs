using System;
using System.Collections.Generic;

namespace ParallelReactor.Api;

/// <summary>一个 sel 字段的枚举项：协议用英文码，中文只是 label（设计稿 §6.9 / §12）。</summary>
public sealed record SchemaOption(string Code, string Label, bool Available = true, string? Reason = null);

/// <summary>
/// 程序 schema 的枚举码 ↔ 界面文案映射表（设计稿 §6.9），与 Models/ProgramStep.cs 的 <c>Options</c> 一一对应
/// （单元测试从源码解析 Options 核对 label 顺序与文字）。另含曲线段字段范围（ProgramView 曲线编辑器的键盘范围）。纯逻辑。
/// </summary>
public static class ProgramSchemaCodes
{
    public const string GasUnavailableReason = "耗气量计算未实现";

    /// <summary>键 "stepType.fieldKey"（stepType 为小写：purge / heat / press / stir / wait / react / end）。</summary>
    public static readonly IReadOnlyDictionary<string, SchemaOption[]> Options = new Dictionary<string, SchemaOption[]>(StringComparer.Ordinal)
    {
        ["purge.gas"] = new[] { new SchemaOption("inert", "惰性气体"), new SchemaOption("A", "气体 A"), new SchemaOption("B", "气体 B") },
        ["press.gas"] = new[] { new SchemaOption("A", "气体 A"), new SchemaOption("B", "气体 B") },
        ["press.regulate"] = new[] { new SchemaOption("on", "开启"), new SchemaOption("off", "关闭") },
        ["wait.mode"] = new[] { new SchemaOption("fixed", "固定时长"), new SchemaOption("temp", "等待温度到位"), new SchemaOption("pressure", "等待压力到位") },
        ["react.endBy"] = new[]
        {
            new SchemaOption("time", "时间"),
            new SchemaOption("gasConsumed", "气体消耗", false, GasUnavailableReason),
            new SchemaOption("either", "两者先到", false, GasUnavailableReason),
        },
        ["end.mode"] = new[] { new SchemaOption("seal", "密封 (推荐)"), new SchemaOption("purge", "吹扫"), new SchemaOption("quench", "淬灭"), new SchemaOption("vent", "排空") },
        // 不在 StepDef.Fields 里、由程序页单独编辑的字段（schema 手工补上）
        ["heat.mode"] = new[] { new SchemaOption("fixed", "定值"), new SchemaOption("curve", "曲线") },
    };

    /// <summary>不可用的数值字段（依赖 GasValid，见设计稿附录 C）。</summary>
    public static readonly IReadOnlySet<string> UnavailableFields = new HashSet<string>(StringComparer.Ordinal) { "react.mmol" };

    /// <summary>曲线段数范围（Models/TempProfile.cs MaxSegments = 16；ProgramView CanAddSeg）。</summary>
    public const int SegmentsMin = 1, SegmentsMax = 16;

    /// <summary>曲线段字段（ProgramViewModel.EditSegTemp / EditSegRate / EditSegHold 的键盘范围）。</summary>
    public static readonly (string Key, string Label, string Unit, double Min, double Max)[] SegmentFields =
    {
        ("temp", "段目标温度", "°C", 0, 300),
        ("rate", "升温速率", "°C/min", 0, 50),
        ("hold", "保温时间", "分钟", 0, 600),
    };

    /// <summary>界面文案 → 枚举码（找不到返回 null）。</summary>
    public static string? CodeOf(string stepType, string key, string label)
    {
        if (!Options.TryGetValue($"{stepType}.{key}", out var opts)) return null;
        foreach (var o in opts) if (o.Label == label) return o.Code;
        return null;
    }

    /// <summary>枚举码 → 界面文案（找不到返回 null）。</summary>
    public static string? LabelOf(string stepType, string key, string code)
    {
        if (!Options.TryGetValue($"{stepType}.{key}", out var opts)) return null;
        foreach (var o in opts) if (o.Code == code) return o.Label;
        return null;
    }
}
