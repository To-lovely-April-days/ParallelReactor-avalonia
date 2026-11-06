using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>步骤参数读取（<see cref="ProgramStep"/> 与 <see cref="StepSnapshot"/> 共用同一套解析，二者不会漂移）。</summary>
public static class StepParams
{
    /// <summary>数值参数：double 直接取；字符串按不变区域解析；缺失或无法解析返回 def。</summary>
    public static double Num(IReadOnlyDictionary<string, object> p, string k, double def = 0)
        => p.TryGetValue(k, out var v)
            ? (v is double d ? d : (double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dp) ? dp : def))
            : def;

    /// <summary>字符串参数：只认 string，缺失或类型不符返回 def。</summary>
    public static string Str(IReadOnlyDictionary<string, object> p, string k, string def = "")
        => p.TryGetValue(k, out var v) && v is string s ? s : def;
}

/// <summary>
/// 下发时冻结的一个程序步骤（C3）：参数字典与曲线段都是独立副本，
/// 下发后再编辑程序（改参数 / 改曲线 / 应用到其他通道 / 删步骤）不影响正在执行的程序。
/// </summary>
public sealed class StepSnapshot
{
    public StepType Type { get; }
    /// <summary>在时间线中的序号（1 起）。</summary>
    public int Ordinal { get; }
    public string Name { get; }
    public string Detail { get; }
    public IReadOnlyDictionary<string, object> Params { get; }
    /// <summary>曲线段（温度 ℃, 斜率 ℃/min, 保温 min），仅升温曲线模式有意义。</summary>
    public IReadOnlyList<(double Temp, double Rate, double Hold)> Segments { get; }

    private StepSnapshot(StepType type, int ordinal, string name, string detail,
        IReadOnlyDictionary<string, object> p, IReadOnlyList<(double, double, double)> segs)
    {
        Type = type; Ordinal = ordinal; Name = name; Detail = detail; Params = p; Segments = segs;
    }

    /// <summary>复制输入（字典 → 新的只读字典，段 → 新数组）：之后修改来源不会影响快照。</summary>
    public static StepSnapshot Create(StepType type, int ordinal, string name, string detail,
        IEnumerable<KeyValuePair<string, object>> parameters,
        IEnumerable<(double Temp, double Rate, double Hold)>? segments = null)
    {
        var d = new Dictionary<string, object>();
        foreach (var kv in parameters) d[kv.Key] = kv.Value;
        var segs = (segments ?? Array.Empty<(double, double, double)>()).ToArray();
        return new StepSnapshot(type, ordinal, name ?? "", detail ?? "",
            new ReadOnlyDictionary<string, object>(d), Array.AsReadOnly(segs));
    }

    public double Num(string k, double def = 0) => StepParams.Num(Params, k, def);
    public string Str(string k, string def = "") => StepParams.Str(Params, k, def);

    /// <summary>升温步骤是否为 16 段曲线模式。</summary>
    public bool IsCurve => Type == StepType.Heat && Str("mode", "fixed") == "curve";
}
