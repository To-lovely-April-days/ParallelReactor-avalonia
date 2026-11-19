using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Api;

namespace ParallelReactor.Models;

/// <summary>字段受哪一个全局上限约束（<see cref="ProgramLimits.FieldRange"/> 按它收紧可输入范围、<see cref="ProgramLimits.CheckStep"/> 按它校验）。</summary>
public enum FieldLimit { None, Temp, Pressure, Rpm }

/// <summary>
/// 字段显示条件（STEPS-SPEC §6 <c>visibleWhen</c>）：同一步骤里选项字段 <see cref="Key"/> 的当前码在 <see cref="In"/> 里才显示 / 才起作用。
/// 例如 hold.temp 只在 until ∈ {tempAbove, tempBelow} 时显示。
/// </summary>
public sealed record VisibleWhen(string Key, string[] In);

/// <summary>
/// 步骤的一个参数字段（STEPS-SPEC §1 的一行）。纯逻辑：触屏检查器、接口 PUT 解析、/program-schema、下发前校验共用这一张表。
/// <list type="bullet">
/// <item>选项字段（Kind = sel）存英文码（同线上格式），界面文字取 <see cref="Options"/> 的 label；</item>
/// <item>压力字段（<see cref="Pressure"/>）一律存 psi（表压），触屏按「设置 → 压力单位」显示与输入（<see cref="Units"/>）；</item>
/// <item><see cref="Min"/> / <see cref="Max"/> 是声明范围，「上限」类字段还按 <see cref="Limit"/> 用设备设置收紧。</item>
/// </list>
/// </summary>
public sealed class FieldDef
{
    public string Key = "";
    public string Label = "";
    /// <summary>num | sel。</summary>
    public string Kind = "num";
    /// <summary>显示单位（压力字段为 "psi" / "psi/h"，触屏换成压力单位显示）。</summary>
    public string? Unit;
    /// <summary>选项（英文码 + 中文 label），取自 <see cref="ProgramSchemaCodes.Options"/>。</summary>
    public SchemaOption[]? Options;
    /// <summary>数值默认值（新建步骤）。</summary>
    public double? Default;
    /// <summary>选项默认码（新建步骤；缺省取第一个选项）。</summary>
    public string? DefaultCode;
    public double Min = 0;
    public double Max = 9999;
    /// <summary>只取整数（次数、转速）。</summary>
    public bool Integer;
    /// <summary>压力类字段：存 psi，显示按压力单位。</summary>
    public bool Pressure;
    public FieldLimit Limit;
    public VisibleWhen? VisibleWhen;
    /// <summary>触屏检查器不按普通字段显示（升温方式由专门的切换行编辑）。</summary>
    public bool CustomEditor;

    public bool IsSel => Kind == "sel";
    public string DefaultSel => DefaultCode ?? (Options is { Length: > 0 } o ? o[0].Code : "");

    /// <summary>码 → 界面文字（找不到原样返回）。</summary>
    public string LabelOf(string code) => Options?.FirstOrDefault(o => o.Code == code)?.Label ?? code;

    /// <summary>界面文字或码 → 码（找不到返回 null）。</summary>
    public string? CodeOf(string text) => Options?.FirstOrDefault(o => o.Code == text || o.Label == text)?.Code;
}

/// <summary>步骤的静态定义（名称 / 角标 / 帮助 / 字段 / 摘要），不含图标与颜色（那两项在 Models/ProgramStep.cs 的 StepDef，含 Avalonia 类型）。</summary>
public sealed class StepSpec
{
    public StepType Type;
    public string Name = "";
    public string Chip = "";
    public string Help = "";
    public List<FieldDef> Fields = new();
    /// <summary>参数摘要（步骤卡片 / 运行监视 / 接口 detail）。</summary>
    public Func<IReadOnlyDictionary<string, object>, IReadOnlyList<(double Temp, double Rate, double Hold)>, string> Detail = (_, _) => "";

    public FieldDef? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);
}

/// <summary>
/// v0.8 的九种步骤（research/fusion-port/notes/STEPS-SPEC.md §1）：字段、单位、默认值、范围、显示条件。
/// 旧码 purge / wait 不在这里——它们只在输入时认，读进来立即按 <see cref="StepMigration"/> 转换。
/// </summary>
public static class StepSpecs
{
    /// <summary>新建气密检查时「允许压降」的默认值（psi/h）：设备设置 LeakRate 的出厂值；触屏 / 接口按当前设置覆盖。</summary>
    public const double LeakRateDefault = 2;
    /// <summary>压力字段的声明上限 psi（34 bar，用户确认的升压目标上限）；实际上限还按 PMaxSp 与超压报警 × 0.9 收紧。</summary>
    public const double PressDeclaredMax = 493.1;
    /// <summary>温度字段的声明上限 ℃（同曲线编辑器）。</summary>
    public const double TempDeclaredMax = 300;
    /// <summary>转速的声明上限 rpm（同设置「搅拌转速上限」的可设范围）；实际上限 = RpmMax。</summary>
    public const double RpmDeclaredMax = 3000;

    public static readonly IReadOnlyDictionary<StepType, StepSpec> Registry = Build();

    public static StepSpec Of(StepType t) => Registry[t];

    public static bool TryGet(StepType t, out StepSpec spec)
    {
        if (Registry.TryGetValue(t, out var s)) { spec = s; return true; }
        spec = null!;
        return false;
    }

    private static SchemaOption[] Opt(string key) => ProgramSchemaCodes.Options[key];

    private static FieldDef Num(string key, string label, string? unit, double def, double min, double max,
        FieldLimit limit = FieldLimit.None, bool integer = false, bool pressure = false, VisibleWhen? vis = null)
        => new() { Key = key, Label = label, Kind = "num", Unit = unit, Default = def, Min = min, Max = max, Limit = limit, Integer = integer, Pressure = pressure, VisibleWhen = vis };

    private static FieldDef Sel(string type, string key, string label, string def, VisibleWhen? vis = null)
        => new() { Key = key, Label = label, Kind = "sel", Options = Opt($"{type}.{key}"), DefaultCode = def, VisibleWhen = vis };

    private static VisibleWhen When(string key, params string[] codes) => new(key, codes);

    private static Dictionary<StepType, StepSpec> Build()
    {
        var holdCond = new[] { "tempAbove", "tempBelow", "pressAbove", "pressBelow" };
        var r = new Dictionary<StepType, StepSpec>
        {
            [StepType.Leak] = new StepSpec
            {
                Type = StepType.Leak, Name = "气密检查", Chip = "LEAK",
                Help = "用惰性气体把这一路充到试验压力，关阀静置后保压计时，按温度补偿后的压降速率判定。保压期间不占用气路，别的路可以同时充气。" +
                       "试验压力建议不低于后面反应的压力；「允许压降」默认取设置里的泄漏率阈值。",
                Fields = new()
                {
                    Num("pres", "试验压力", "psi", 435.1, 14.5, PressDeclaredMax, FieldLimit.Pressure, pressure: true),
                    Num("settle", "稳定时间", "分钟", 5, 0, 60),
                    Num("hold", "保压时间", "分钟", 30, 5, 600),
                    Num("maxDrop", "允许压降", "psi/h", LeakRateDefault, 0.1, 100, pressure: true),
                    Sel("leak", "onFail", "不通过时", "stop"),
                    Sel("leak", "after", "之后", "vent"),
                },
                Detail = (p, _) => $"惰性气体 {P(p, "pres", 435.1)} · 稳定 {N(Num(p, "settle", 5))} min · 保压 {N(Num(p, "hold", 30))} min"
                                   + $" · ≤ {PRate(Num(p, "maxDrop", LeakRateDefault))} · 不通过{(Str(p, "onFail", "stop") == "continue" ? "记录后继续" : "停止这一路")}"
                                   + (Str(p, "after", "vent") == "keep" ? " · 之后保持压力" : " · 之后放空"),
            },
            [StepType.Exchange] = new StepSpec
            {
                Type = StepType.Exchange, Name = "置换", Chip = "EXCHANGE",
                Help = "把釜充到「充到」再放到「放到」以下，重复「次数」次，真正换掉釜里的气；可先吹扫总管（SV 关着，把上一种气赶出总管）。" +
                       "单次充或放超过「单次最长」就报警停止这一路（充不上去：检查气源与减压阀；放不下来：可能有进气单向阀，不要开釜）。次数 0 = 只吹总管。",
                Fields = new()
                {
                    Sel("exchange", "gas", "气体", "inert"),
                    Num("fill", "充到", "psi", 72.5, 7.3, PressDeclaredMax, FieldLimit.Pressure, pressure: true),
                    Num("release", "放到", "psi", 2.9, 0, PressDeclaredMax, pressure: true),
                    Num("cycles", "次数", "次", 3, 0, 20, integer: true),
                    Num("lineFlush", "先吹扫总管", "次", 2, 0, 10, integer: true),
                    Num("maxSec", "单次最长", "秒", 60, 10, 300),
                },
                Detail = (p, _) =>
                {
                    string gas = GasLabel("exchange", Str(p, "gas", "inert"));
                    int cycles = (int)Math.Round(Num(p, "cycles", 3)), flush = (int)Math.Round(Num(p, "lineFlush", 2));
                    string head = cycles <= 0
                        ? $"{gas} · 只吹总管 {flush} 次（釜不置换）"
                        : $"{gas} · {P(p, "fill", 72.5)} → {P(p, "release", 2.9)} × {cycles}";
                    return cycles > 0 && flush > 0 ? $"{head} · 先吹总管 {flush} 次" : head;
                },
            },
            [StepType.Stir] = new StepSpec
            {
                Type = StepType.Stir, Name = "搅拌", Chip = "STIR",
                Help = "设定釜里的转速（电机 = 釜 × 传动比），立即进入下一步，转速一直保持到下一个「搅拌」或程序结束；0 = 停。" +
                       "一台电机带全部釜：同时运行的几路程序转速应一致，改写别的程序正在用的转速会记一条提醒。要「搅 10 分钟」用「搅拌 → 保持 10 min」。",
                Fields = new()
                {
                    Num("rpm", "转速", "rpm", 400, 0, RpmDeclaredMax, FieldLimit.Rpm, integer: true),
                },
                Detail = (p, _) => Num(p, "rpm", 400) <= 0 ? "停止搅拌" : $"{N(Num(p, "rpm", 400))} rpm · 保持到下一个搅拌",
            },
            [StepType.Heat] = new StepSpec
            {
                Type = StepType.Heat, Name = "升温", Chip = "HEAT",
                Help = "定值：升到目标温度，实测进入「到位范围」后再平衡「到位后平衡」分钟（让溶液温度均匀）；升温速率 0 = 尽快（防过冲逼近）。" +
                       "曲线：跑完各段后同样等实测温度到位（最后一段的温度）。超过「最长等待」仍未到位按「超时」处理。加热一直保持到下一个升温或程序结束。",
                Fields = new()
                {
                    new FieldDef { Key = "mode", Label = "升温方式", Kind = "sel", Options = Opt("heat.mode"), DefaultCode = "fixed", CustomEditor = true },
                    Num("temp", "目标温度", "°C", 80, 35, TempDeclaredMax, FieldLimit.Temp, vis: When("mode", "fixed")),
                    Num("rate", "升温速率", "°C/min", 0, 0, 20, vis: When("mode", "fixed")),
                    Num("band", "到位范围 ±", "°C", 2, 0.5, 10),
                    Num("wait", "到位后平衡", "分钟", 15, 0, 120, vis: When("mode", "fixed")),
                    Num("maxWait", "最长等待", "分钟", 120, 10, 600),
                    Sel("heat", "onTimeout", "超时", "stop"),
                },
                Detail = (p, segs) =>
                {
                    string band = $"±{N(Num(p, "band", 2))}°C";
                    if (Str(p, "mode", "fixed") == "curve" && segs.Count > 0)
                        return $"16 段曲线 · {segs.Count} 段 · 终温 {N(segs[^1].Temp)}°C · ≈ {N(Math.Round(CurveMinutes(segs)))} min · 到位 {band}";
                    double rate = Num(p, "rate", 0);
                    return $"定值 {N(Num(p, "temp", 80))}°C" + (rate > 0 ? $" · {N(rate)}°C/min" : "") + $" · {band} · 平衡 {N(Num(p, "wait", 15))} min";
                },
            },
            [StepType.Press] = new StepSpec
            {
                Type = StepType.Press, Name = "充压", Chip = "PRESS",
                Help = "用所选气体充到目标压力（从下方逼近，到「允许偏差」以内算到位）；只能升、不能降，已经高于目标时不动并写明。" +
                       "稳压开 = 压力低于目标 ×（1 − 补气阈值）时自动补气，一直到下一个充压 / 置换 / 放空 / 气密检查或结束；补进去的气不算吸气量。" +
                       "升温会让压力上去，建议先升温到位再充压。",
                Fields = new()
                {
                    Sel("press", "gas", "气体", "A"),
                    Num("pres", "目标压力", "psi", 290.1, 7.3, PressDeclaredMax, FieldLimit.Pressure, pressure: true),
                    Num("tol", "允许偏差", "%", 2, 0.5, 10),
                    Sel("press", "regulate", "之后稳压", "on"),
                    Num("refill", "补气阈值", "%", 3, 1, 10, vis: When("regulate", "on")),
                    Num("maxSec", "最长充气", "秒", 180, 30, 600),
                },
                Detail = (p, _) => $"{GasLabel("press", Str(p, "gas", "A"))} 到 {P(p, "pres", 290.1)} · 稳压{(Str(p, "regulate", "on") == "off" ? "关" : "开")}",
            },
            [StepType.Hold] = new StepSpec
            {
                Type = StepType.Hold, Name = "保持", Chip = "HOLD",
                Help = "什么都不动，等条件：时间到，或温度 / 压力达到步骤里写的目标值（每 1 s 判断一次）。" +
                       "按温度 / 压力等待时，超过「最长等待」或传感器离线超过 60 s 按「超时」处理。",
                Fields = new()
                {
                    Sel("hold", "until", "结束条件", "time"),
                    Num("time", "时间", "分钟", 10, 0, 1440, vis: When("until", "time")),
                    Num("temp", "目标温度", "°C", 40, 0, TempDeclaredMax, FieldLimit.Temp, vis: When("until", "tempAbove", "tempBelow")),
                    Num("pres", "目标压力", "psi", 14.5, 0, PressDeclaredMax, FieldLimit.Pressure, pressure: true, vis: When("until", "pressAbove", "pressBelow")),
                    Num("maxWait", "最长等待", "分钟", 60, 1, 1440, vis: When("until", holdCond)),
                    Sel("hold", "onTimeout", "超时", "continue", vis: When("until", holdCond)),
                },
                Detail = (p, _) =>
                {
                    string until = Str(p, "until", "time");
                    string cap = $" · 最长 {N(Num(p, "maxWait", 60))} min";
                    return until switch
                    {
                        "tempAbove" => $"温度 ≥ {N(Num(p, "temp", 40))}°C{cap}",
                        "tempBelow" => $"温度 ≤ {N(Num(p, "temp", 40))}°C{cap}",
                        "pressAbove" => $"压力 ≥ {P(p, "pres", 14.5)}{cap}",
                        "pressBelow" => $"压力 ≤ {P(p, "pres", 14.5)}{cap}",
                        _ => $"时间 {N(Num(p, "time", 10))} min",
                    };
                },
            },
            [StepType.React] = new StepSpec
            {
                Type = StepType.React, Name = "反应", Chip = "REACT",
                Help = "不动阀、不改温度：温度靠前面的升温保持，压力靠充压的稳压，搅拌靠前面的搅拌。每 2 s 判断一次，先到者结束：最长时间到；" +
                       "过了最短时间且吸气量达到目标；过了最短时间且吸气速率连续「持续」分钟低于「停止速率」。反应前没有充压时吸气量不计量，只按最长时间结束。",
                Fields = new()
                {
                    Num("time", "最长时间", "分钟", 480, 1, 4320),
                    Num("minTime", "最短时间", "分钟", 30, 0, 4320),
                    Sel("react", "mmolOn", "吸气量达到", "off"),
                    Num("mmol", "目标吸气量", "mmol", 5, 0.01, 100, vis: When("mmolOn", "on")),
                    Sel("react", "stallOn", "吸气停止", "on"),
                    Num("stallRate", "停止速率", "mmol/min", 0.005, 0.001, 1, vis: When("stallOn", "on")),
                    Num("stallMin", "持续", "分钟", 30, 5, 240, vis: When("stallOn", "on")),
                },
                Detail = (p, _) =>
                {
                    var parts = new List<string> { $"最长 {N(Num(p, "time", 480))} min", $"最短 {N(Num(p, "minTime", 30))} min" };
                    if (Str(p, "mmolOn", "off") == "on") parts.Add($"吸气量 {N(Num(p, "mmol", 5))} mmol");
                    if (Str(p, "stallOn", "on") == "on") parts.Add($"吸气停止 < {N(Num(p, "stallRate", 0.005))} mmol/min 持续 {N(Num(p, "stallMin", 30))} min");
                    return string.Join(" · ", parts);
                },
            },
            [StepType.Vent] = new StepSpec
            {
                Type = StepType.Vent, Name = "放空", Chip = "VENT",
                Help = "开 SVn + 排空阀，读到釜压 ≤「放到」才算放完；超过「最长时间」还放不下来就报警停止这一路（釜里的气放不掉，不要开釜）。" +
                       "之后可用惰性气体置换几次，把残余的反应气赶走再开釜。釜温高于 60 °C 时放空会带出溶剂蒸气（不拦，可在前面加「保持 · 温度 ≤ x」）。",
                Fields = new()
                {
                    Num("to", "放到", "psi", 2.9, 0, 72.5, pressure: true),
                    Num("maxSec", "最长时间", "秒", 120, 10, 600),
                    Num("flush", "之后惰性置换", "次", 3, 0, 10, integer: true),
                    Num("flushPres", "置换充到", "psi", 43.5, 7.3, PressDeclaredMax, FieldLimit.Pressure, pressure: true),
                },
                Detail = (p, _) =>
                {
                    int flush = (int)Math.Round(Num(p, "flush", 3));
                    return $"放到 {P(p, "to", 2.9)} · " + (flush > 0 ? $"之后惰性置换 × {flush}（{P(p, "flushPres", 43.5)}）" : "不置换");
                },
            },
            [StepType.End] = new StepSpec
            {
                Type = StepType.End, Name = "结束", Chip = "END",
                Help = "固定的最后一步（不能删、不能挪）：关加热、所有阀关、释放搅拌（别的路还在用就继续转）、停止吸气计量、写结束原因。" +
                       "不降温：结束时釜可能还是热的；需要等凉了再放空，可在放空前加「保持 · 温度 ≤ x」。",
                Fields = new(),
                Detail = (_, _) => "关加热、阀全关、停搅拌",
            },
        };
        return r;
    }

    // ============ 参数小件（与 StepParams 同一解析规则）============

    public static double Num(IReadOnlyDictionary<string, object> p, string k, double def) => StepParams.Num(p, k, def);
    public static string Str(IReadOnlyDictionary<string, object> p, string k, string def) => StepParams.Str(p, k, def);

    /// <summary>新建步骤的参数（数值 = 默认值，选项 = 默认码）。</summary>
    public static Dictionary<string, object> Defaults(StepType t)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!Registry.TryGetValue(t, out var spec)) return d;
        foreach (var f in spec.Fields)
        {
            if (f.IsSel) d[f.Key] = f.DefaultSel;
            else if (f.Default is double v) d[f.Key] = v;
        }
        return d;
    }

    /// <summary>字段在当前参数下是否显示 / 起作用（visibleWhen）；控制字段没写时按它的默认码判断。</summary>
    public static bool IsVisible(StepType t, FieldDef f, IReadOnlyDictionary<string, object> p)
    {
        if (f.VisibleWhen is not { } w) return true;
        string cur = Str(p, w.Key, "");
        if (cur.Length == 0 && Registry.TryGetValue(t, out var spec)) cur = spec.Field(w.Key)?.DefaultSel ?? "";
        return Array.IndexOf(w.In, cur) >= 0;
    }

    /// <summary>参数摘要。</summary>
    public static string Detail(StepType t, IReadOnlyDictionary<string, object> p, IReadOnlyList<(double Temp, double Rate, double Hold)> segs)
        => Registry.TryGetValue(t, out var spec) ? spec.Detail(p, segs) : t.Zh();

    /// <summary>曲线总时长（分钟，从 25 ℃ 起算；同曲线编辑器的预览）。</summary>
    public static double CurveMinutes(IReadOnlyList<(double Temp, double Rate, double Hold)> segs, double startTemp = 25)
    {
        double total = 0, prev = startTemp;
        foreach (var s in segs)
        {
            total += (s.Rate > 0 ? Math.Abs(s.Temp - prev) / s.Rate : 0) + Math.Max(0, s.Hold);
            prev = s.Temp;
        }
        return total;
    }

    /// <summary>气体码 → 界面文字（惰性气体 / 气体 A / 气体 B）。</summary>
    public static string GasLabel(string type, string code) => ProgramSchemaCodes.LabelOf(type, "gas", code) ?? code;

    /// <summary>气体码 → 气源下标（0 = 惰性，1 = A，2 = B；同 <see cref="ValveFrame"/>）。</summary>
    public static int GasIndex(string code) => code switch { "A" => 1, "B" => 2, _ => 0 };

    /// <summary>数字格式化：整数不带小数，其余最多 3 位。</summary>
    public static string N(double v) =>
        !double.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture)
        : v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture)
        : v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string P(IReadOnlyDictionary<string, object> p, string k, double def) => Units.FormatP(Num(p, k, def));

    /// <summary>压降速率 psi/h → 当前压力单位 /h。</summary>
    public static string PRate(double psiPerHour) => $"{Units.PValue(psiPerHour)} {Units.PLabel}/h";
}

/// <summary>
/// 程序时长估算（STEPS-SPEC §5，HMI 与 PC 同一套，单位分钟）。<see cref="Approx"/> = 含按条件结束的保持（标「约」），
/// <see cref="UpperBound"/> = 含反应（按最长时间估，标「≤」）。
/// </summary>
public readonly record struct DurationEstimate(double Minutes, bool Approx, bool UpperBound)
{
    public static DurationEstimate operator +(DurationEstimate a, DurationEstimate b)
        => new(a.Minutes + b.Minutes, a.Approx || b.Approx, a.UpperBound || b.UpperBound);

    /// <summary>「约 ≤ 9h 10m」这样的文字（没有步骤时「—」）。</summary>
    public string Text
    {
        get
        {
            int total = (int)Math.Round(Minutes);
            return (Approx ? "约 " : "") + (UpperBound ? "≤ " : "") + $"{total / 60}h {total % 60}m";
        }
    }
}

public static class StepDuration
{
    /// <summary>一步的估算。</summary>
    public static DurationEstimate Of(StepType t, IReadOnlyDictionary<string, object> p, IReadOnlyList<(double Temp, double Rate, double Hold)> segs)
    {
        double Num(string k, double d) => StepParams.Num(p, k, d);
        switch (t)
        {
            case StepType.Leak: return new(Num("settle", 5) + Num("hold", 30) + 1, false, false);
            case StepType.Exchange: return new((Num("lineFlush", 2) * 9 + Num("cycles", 3) * 20) / 60.0, false, false);
            case StepType.Heat:
                if (StepParams.Str(p, "mode", "fixed") == "curve") return new(StepSpecs.CurveMinutes(segs), false, false);
                double rate = Num("rate", 0);
                return new(Math.Abs(Num("temp", 80) - 25) / (rate > 0 ? rate : 3.5) + Num("wait", 15), false, false);
            case StepType.Press: return new(3, false, false);
            case StepType.Hold:
                return StepParams.Str(p, "until", "time") == "time"
                    ? new(Num("time", 10), false, false)
                    : new(Math.Min(Num("maxWait", 60), 15), true, false);
            case StepType.React: return new(Num("time", 480), false, true);
            case StepType.Vent: return new(1 + Num("flush", 3) * 20 / 60.0, false, false);
            default: return new(0, false, false);   // stir / end（旧码不估算）
        }
    }

    public static DurationEstimate Of(StepSnapshot s) => Of(s.Type, s.Params, s.Segments);

    public static DurationEstimate Total(IEnumerable<StepSnapshot> steps)
    {
        var sum = new DurationEstimate(0, false, false);
        foreach (var s in steps) sum += Of(s);
        return sum;
    }
}

/// <summary>推荐模板（设计稿 §五；用户 2026-11-19 去掉了降温步骤）。参数只写与默认值不同的项，其余取 <see cref="StepSpecs.Defaults"/>。</summary>
public static class StepTemplates
{
    public sealed record TemplateStep(StepType Type, IReadOnlyDictionary<string, object> Params);

    private static TemplateStep T(StepType t, params (string K, object V)[] kv)
    {
        var p = StepSpecs.Defaults(t);
        foreach (var (k, v) in kv) p[k] = v;
        return new TemplateStep(t, p);
    }

    /// <summary>
    /// 加氢筛选（默认模板）：气密检查 惰性 3.0 MPa · 稳定 5 · 保压 30 → 置换 惰性 ×3 → 置换 气体 A ×3 → 搅拌 400 →
    /// 升温 80 ℃ ±2 平衡 15 → 充压 气体 A 2.0 MPa 稳压开 → 反应 最长 8 h · 最短 30 min · 吸气停止 → 放空 0.02 MPa + 惰性置换 ×3 → 结束。
    /// </summary>
    public static IReadOnlyList<TemplateStep> Hydrogenation(double leakRate = StepSpecs.LeakRateDefault) => new[]
    {
        T(StepType.Leak, ("maxDrop", leakRate)),
        T(StepType.Exchange, ("gas", "inert")),
        T(StepType.Exchange, ("gas", "A")),
        T(StepType.Stir),
        T(StepType.Heat),
        T(StepType.Press),
        T(StepType.React),
        T(StepType.Vent),
        T(StepType.End),
    };
}
