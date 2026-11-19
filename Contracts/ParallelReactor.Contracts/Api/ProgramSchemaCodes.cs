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
    /// <summary>历史原因：v0.5 之前耗气量没有实现时的说明文字。耗气量已由 HMI 按压降计算（Models/GasUptake.cs），目前没有不可用的选项；保留常量供旧客户端引用。</summary>
    public const string GasUnavailableReason = "耗气量计算未实现";

    /// <summary>键 "stepType.fieldKey"（stepType 为小写：purge / heat / press / stir / wait / react / end）。</summary>
    public static readonly IReadOnlyDictionary<string, SchemaOption[]> Options = BuildOptions();

    private static SchemaOption[] Gas3() => new[] { new SchemaOption("inert", "惰性气体"), new SchemaOption("A", "气体 A"), new SchemaOption("B", "气体 B") };
    private static SchemaOption[] OnOff() => new[] { new SchemaOption("on", "开启"), new SchemaOption("off", "关闭") };

    /// <summary>
    /// v0.8 的选项（STEPS-SPEC.md §1）+ 旧格式的选项（purge / wait / react.endBy / end.mode：只在输入时认，转换用，§3）。
    /// </summary>
    private static Dictionary<string, SchemaOption[]> BuildOptions() => new(StringComparer.Ordinal)
    {
        // —— v0.8 ——
        ["leak.onFail"] = new[] { new SchemaOption("stop", "停止这一路"), new SchemaOption("continue", "记录后继续") },
        ["leak.after"] = new[] { new SchemaOption("vent", "放空"), new SchemaOption("keep", "保持压力") },
        ["exchange.gas"] = Gas3(),
        ["heat.mode"] = new[] { new SchemaOption("fixed", "定值"), new SchemaOption("curve", "曲线") },
        ["heat.onTimeout"] = new[] { new SchemaOption("stop", "停止这一路"), new SchemaOption("continue", "继续") },
        ["press.gas"] = Gas3(),
        ["press.regulate"] = OnOff(),
        ["hold.until"] = new[]
        {
            new SchemaOption("time", "时间到"), new SchemaOption("tempAbove", "温度 ≥"), new SchemaOption("tempBelow", "温度 ≤"),
            new SchemaOption("pressAbove", "压力 ≥"), new SchemaOption("pressBelow", "压力 ≤"),
        },
        ["hold.onTimeout"] = new[] { new SchemaOption("continue", "继续"), new SchemaOption("stop", "停止这一路") },
        ["react.mmolOn"] = OnOff(),
        ["react.stallOn"] = OnOff(),
        // —— 旧格式（输入时认，转换用） ——
        ["purge.gas"] = Gas3(),
        ["wait.mode"] = new[] { new SchemaOption("fixed", "固定时长"), new SchemaOption("temp", "等待温度到位"), new SchemaOption("pressure", "等待压力到位") },
        ["react.endBy"] = new[] { new SchemaOption("time", "时间"), new SchemaOption("gasConsumed", "气体消耗"), new SchemaOption("either", "两者先到") },
        ["end.mode"] = new[] { new SchemaOption("seal", "密封 (推荐)"), new SchemaOption("purge", "吹扫"), new SchemaOption("quench", "淬灭"), new SchemaOption("vent", "排空") },
    };

    /// <summary>旧格式才有的选项键（/program-schema 不列出）。</summary>
    public static readonly IReadOnlySet<string> LegacyKeys = new HashSet<string>(StringComparer.Ordinal) { "purge.gas", "wait.mode", "react.endBy", "end.mode" };

    /// <summary>不可用的数值字段（依赖 GasValid，见设计稿附录 C）。耗气量已实现（升压完成后按压降计量），目前为空。</summary>
    public static readonly IReadOnlySet<string> UnavailableFields = new HashSet<string>(StringComparer.Ordinal);

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
