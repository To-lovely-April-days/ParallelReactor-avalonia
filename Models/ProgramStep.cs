using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Models;

/// <summary>升温曲线的单段（16 段曲线编辑器用）。</summary>
public partial class HeatSeg : ObservableObject
{
    [ObservableProperty] private int _no;
    [ObservableProperty] private double _temp;
    [ObservableProperty] private double _rate;
    [ObservableProperty] private double _hold;

    public HeatSeg Clone() => new() { No = No, Temp = Temp, Rate = Rate, Hold = Hold };
}

/// <summary>检查器字段定义，对应 STEP_DEFS[x].fields 的每一项。</summary>
public class FieldDef
{
    public string Key = "";
    public string Label = "";
    public string Kind = "num";       // num | sel
    public string? Unit;
    public string[]? Options;
    public object? Default;
    public double Min = 0;            // 数值字段范围（供屏幕键盘裁剪与提示）
    public double Max = 9999;
}

/// <summary>步骤静态定义（名称 / 角标 / 图标 / 字段 / 详情 / 帮助），对应 STEP_DEFS。</summary>
public class StepDef
{
    public StepType Type;
    public string Name = "";
    public string Chip = "";
    public string Help = "";
    public Geometry Icon = Geometry.Parse("M6 6h12v12H6z");
    public IBrush Brush = Brushes.Gray;
    public List<FieldDef> Fields = new();
    public Func<ProgramStep, string> DetailFn = _ => "";

    public static readonly Dictionary<StepType, StepDef> Registry = Build();

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Geometry G(string d) => Geometry.Parse(d);

    /// <summary>数字格式化：整数不带小数。</summary>
    public static string N(double v) =>
        v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture)
                           : v.ToString("0.#", CultureInfo.InvariantCulture);

    private static Dictionary<StepType, StepDef> Build()
    {
        var r = new Dictionary<StepType, StepDef>
        {
            [StepType.Purge] = new StepDef
            {
                Type = StepType.Purge, Name = "吹扫", Chip = "PURGE", Brush = B("#6f93b8"),
                Icon = G("M9.6 4.6A2 2 0 1 1 11 8H2 M12.6 19.4A2 2 0 1 0 14 16H2 M17.5 8A2.5 2.5 0 1 1 19.5 12.5H2"),
                Help = "只吹扫气路总管：8 路 SV 全程关闭，釜内气体不受影响。每次循环：开气源阀充气 → 关闭 → 开排空阀排气。" +
                       "总管压力等于该气源减压阀的出口压力，请预先调到低压。",
                Fields = new()
                {
                    new FieldDef{ Key="gas", Label="气体", Kind="sel", Options=new[]{"惰性气体","气体 A","气体 B"} },
                    new FieldDef{ Key="cycles", Label="循环次数", Kind="num", Unit="次", Default=10.0, Min=1, Max=50 },
                    new FieldDef{ Key="fill", Label="充气时间", Kind="num", Unit="秒", Default=3.0, Min=1, Max=30 },
                    new FieldDef{ Key="vent", Label="排气时间", Kind="num", Unit="秒", Default=5.0, Min=1, Max=60 },
                },
                DetailFn = s => $"{s.Str("gas","惰性气体")} × {N(s.Num("cycles",10))} 次 · 充 {N(s.Num("fill",3))}s / 排 {N(s.Num("vent",5))}s · 仅总管",
            },
            [StepType.Heat] = new StepDef
            {
                Type = StepType.Heat, Name = "升温", Chip = "HEAT", Brush = B("#c79450"),
                Icon = G("M14 14.76V3.5a2.5 2.5 0 0 0-5 0v11.26a4.5 4.5 0 1 0 5 0z"),
                Help = "先升温至目标（实测到位 ±2℃ 才进下一步）；「等待平衡」为到位后的保温计时，用于让溶液温度均匀（建议 15-25 分钟，0 = 不平衡）。",
                Fields = new()
                {
                    new FieldDef{ Key="temp", Label="目标温度", Kind="num", Unit="°C", Default=150.0, Min=0, Max=300 },
                    new FieldDef{ Key="wait", Label="等待平衡", Kind="num", Unit="分钟", Default=15.0, Min=0, Max=120 },
                },
                DetailFn = s =>
                {
                    if (s.Str("mode","fixed") == "curve" && s.Segments.Count > 0)
                    {
                        var last = s.Segments[^1];
                        double total = 0, prevT = 25;
                        foreach (var seg in s.Segments)
                        {
                            double ramp = seg.Rate > 0 ? Math.Abs(seg.Temp - prevT) / seg.Rate : 0;
                            total += ramp + seg.Hold; prevT = seg.Temp;
                        }
                        return $"16 段曲线 · {s.Segments.Count} 段 · 终温 {N(last.Temp)}°C · ≈ {N(Math.Round(total))} min";
                    }
                    return $"定值 {N(s.Num("temp",150))}°C · 平衡 {N(s.Num("wait",15))} min";
                },
            },
            [StepType.Press] = new StepDef
            {
                Type = StepType.Press, Name = "升压", Chip = "PRESS", Brush = B("#5f87b5"),
                Icon = G("M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0"),
                Help = "稳压建议：低气体消耗反应（< 0.5 mmol/h）关闭稳压可获更低 rsd。",
                Fields = new()
                {
                    new FieldDef{ Key="gas", Label="反应气", Kind="sel", Options=new[]{"气体 A","气体 B"} },
                    new FieldDef{ Key="pres", Label="目标压力", Kind="num", Unit="psi", Default=50.0, Min=0, Max=200 },
                    new FieldDef{ Key="regulate", Label="压力稳压", Kind="sel", Options=new[]{"开启","关闭"} },
                },
                DetailFn = s => $"{s.Str("gas","气体 A")} 到 {N(s.Num("pres",50))} psi · 稳压{s.Str("regulate","开启")}",
            },
            [StepType.Stir] = new StepDef
            {
                Type = StepType.Stir, Name = "搅拌", Chip = "STIR", Brush = B("#7e86a8"),
                Icon = G("M21 12a9 9 0 1 1-6.22-8.56 M21 3v6h-6"),
                Help = "搅拌为全机共用一台电机。步骤持续「搅拌时长」后自动停止搅拌并进入下一步；需要持续搅拌时可在后续步骤再次添加搅拌。",
                Fields = new()
                {
                    new FieldDef{ Key="rpm", Label="转速", Kind="num", Unit="rpm", Default=1000.0, Min=0, Max=2000 },
                    new FieldDef{ Key="dur", Label="搅拌时长", Kind="num", Unit="分钟", Default=5.0, Min=0, Max=600 },
                },
                DetailFn = s => $"{N(s.Num("rpm",1000))} rpm · {N(s.Num("dur",5))} min",
            },
            [StepType.Wait] = new StepDef
            {
                Type = StepType.Wait, Name = "等待", Chip = "WAIT", Brush = B("#6b7079"),
                Icon = G("M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 6v6l4 2"),
                Help = "用「等待温度/压力到位」可让程序自适应实际耗时，比固定时长更稳健。",
                Fields = new()
                {
                    new FieldDef{ Key="mode", Label="条件", Kind="sel", Options=new[]{"固定时长","等待温度到位","等待压力到位"} },
                    new FieldDef{ Key="time", Label="最长时间", Kind="num", Unit="分钟", Default=30.0, Min=0, Max=600 },
                },
                DetailFn = s => $"{s.Str("mode","固定时长")} · {N(s.Num("time",30))} min",
            },
            [StepType.React] = new StepDef
            {
                Type = StepType.React, Name = "反应", Chip = "REACT", Brush = B("#6f9f6a"),
                Icon = G("M8.5 2h7 M10 2v6.5L5.6 18a2 2 0 0 0 1.8 3h9.2a2 2 0 0 0 1.8-3L14 8.5V2 M7 15h10"),
                Help = "结束条件中选「两者先到」可避免反应完成后还在继续加热消耗气体。",
                Fields = new()
                {
                    new FieldDef{ Key="endBy", Label="结束条件", Kind="sel", Options=new[]{"时间","气体消耗","两者先到"} },
                    new FieldDef{ Key="time", Label="最长时间", Kind="num", Unit="分钟", Default=120.0, Min=0, Max=600 },
                    new FieldDef{ Key="mmol", Label="目标 mmol", Kind="num", Unit="mmol", Default=5.0, Min=0, Max=100 },
                },
                DetailFn = s => $"{s.Str("endBy","两者先到")} · {N(s.Num("time",120))} min 或 {N(s.Num("mmol",5))} mmol",
            },
            [StepType.End] = new StepDef
            {
                Type = StepType.End, Name = "结束", Chip = "END", Brush = B("#b0555f"),
                Icon = G("M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z M4 22v-7"),
                Help = "强烈推荐使用「密封」：高温下吹扫或排空会让溶剂蒸气污染气路 manifold。",
                Fields = new()
                {
                    new FieldDef{ Key="mode", Label="方式", Kind="sel", Options=new[]{"密封 (推荐)","吹扫","淬灭","排空"} },
                    new FieldDef{ Key="cool", Label="先降温至", Kind="num", Unit="°C", Default=40.0, Min=0, Max=300 },
                },
                DetailFn = s => $"先降至 {N(s.Num("cool",40))}°C · {s.Str("mode","密封 (推荐)")}",
            },
        };
        return r;
    }
}

/// <summary>一个具体的程序步骤实例（携带参数值）。</summary>
public partial class ProgramStep : ObservableObject
{
    public StepType Type { get; }
    private readonly Dictionary<string, object> _p = new();

    /// <summary>在时间线中的序号（1 起），由 VM 重排后写入，用于显示 01 / 02 …。</summary>
    [ObservableProperty] private int _ordinal;
    public string IndexText => Ordinal.ToString("00");
    partial void OnOrdinalChanged(int value) => OnPropertyChanged(nameof(IndexText));

    /// <summary>升温曲线段（仅升温步骤 curve 模式用）。</summary>
    public List<HeatSeg> Segments { get; } = new();

    public event Action? Changed;

    public ProgramStep(StepType type) { Type = type; }

    /// <summary>按字段默认值新建步骤（对应拖入新步骤时的初始化）。</summary>
    public static ProgramStep New(StepType type)
    {
        var s = new ProgramStep(type);
        foreach (var f in StepDef.Registry[type].Fields)
        {
            if (f.Default != null) s._p[f.Key] = f.Default;
            else if (f.Options is { Length: > 0 }) s._p[f.Key] = f.Options[0];
        }
        return s;
    }

    public ProgramStep With(string k, object v) { _p[k] = v; return this; }

    // 解析规则与下发快照共用（StepParams），二者不会漂移
    public double Num(string k, double def = 0) => StepParams.Num(_p, k, def);

    public string Str(string k, string def = "") => StepParams.Str(_p, k, def);

    public void SetNum(string k, double v) { _p[k] = v; RaiseChanged(); }
    public void SetStr(string k, string v) { _p[k] = v; RaiseChanged(); }

    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(Detail));
        Changed?.Invoke();
    }

    public StepDef Def => StepDef.Registry[Type];
    public string Name => Def.Name;
    public string Chip => Def.Chip;
    public Geometry Icon => Def.Icon;
    public IBrush IconBrush => Def.Brush;
    public string Detail => Def.DetailFn(this);

    /// <summary>该步骤计入总时长的分钟数（对应 totalMin 估算）。</summary>
    public double DurationMin => Type switch
    {
        StepType.Heat => Num("wait", 0) + 15,
        StepType.Press => 8,
        StepType.React => Num("time", 0),
        StepType.Wait => Num("time", 0),
        StepType.Stir => Num("dur", 0),
        // 每次循环 = 充气 + 排气 + 两次 0.5s 阀态切换死区
        StepType.Purge => Num("cycles", 10) * (Num("fill", 3) + Num("vent", 5) + 1.0) / 60.0,
        _ => 0
    };

    public void EnsureSegments()
    {
        if (Segments.Count == 0)
        {
            Segments.Add(new HeatSeg { Temp = 80, Rate = 5, Hold = 10 });
            Segments.Add(new HeatSeg { Temp = 130, Rate = 3, Hold = 20 });
            Segments.Add(new HeatSeg { Temp = 150, Rate = 2, Hold = 30 });
            Segments.Add(new HeatSeg { Temp = 150, Rate = 0, Hold = 60 });
        }
    }

    /// <summary>
    /// 把升温步骤的 HeatSeg 列表（目标温度 / 升温斜率 / 保温时长）换算为曲线引擎的分段（温度, 分钟）。
    /// 纯逻辑见 <see cref="HeatCurvePlan"/>（无 UI 依赖、有单测）。超 max 段截断；无有效段返回空列表（调用方拒绝下发）。
    /// </summary>
    public static List<(double Temp, double Minutes)> ToProfileSegments(
        IReadOnlyList<HeatSeg> segs, double startTemp, int max = TempProfile.MaxSegments)
        => HeatCurvePlan.ToProfileSegments(
            segs.Select(s => (s.Temp, s.Rate, s.Hold)).ToList(), startTemp, max);

    /// <summary>
    /// 下发用的不可变快照（C3）：基于 <see cref="Clone"/> 生成，曲线模式下缺省段只补在副本上（不改编辑中的步骤）。
    /// 之后再编辑本步骤不影响快照。
    /// </summary>
    public StepSnapshot ToSnapshot()
    {
        var c = Clone();
        c.Ordinal = Ordinal;
        if (c.Type == StepType.Heat && c.Str("mode", "fixed") == "curve") c.EnsureSegments();
        return StepSnapshot.Create(c.Type, c.Ordinal, c.Name, c.Detail, c._p,
            c.Segments.Select(g => (g.Temp, g.Rate, g.Hold)));
    }

    /// <summary>由快照还原为可编辑的步骤（远程整体替换程序用，见 OperatorCommands.TryReplaceProgram）。</summary>
    public static ProgramStep FromSnapshot(StepSnapshot snap)
    {
        var s = new ProgramStep(snap.Type);
        foreach (var kv in snap.Params) s._p[kv.Key] = kv.Value;
        foreach (var (t, r, h) in snap.Segments) s.Segments.Add(new HeatSeg { Temp = t, Rate = r, Hold = h });
        return s;
    }

    /// <summary>深拷贝（应用到其他通道用）。</summary>
    public ProgramStep Clone()
    {
        var c = new ProgramStep(Type);
        foreach (var kv in _p) c._p[kv.Key] = kv.Value;
        foreach (var seg in Segments) c.Segments.Add(seg.Clone());
        return c;
    }
}

/// <summary>检查器里一行可编辑字段（num / sel），双向绑定回 ProgramStep。</summary>
public partial class StepField : ObservableObject
{
    public string Label { get; }
    public string? Unit { get; }
    public string Kind { get; }                 // num | sel
    public string[] Options { get; }
    public bool IsNum => Kind == "num";
    public bool IsSel => Kind == "sel";

    public double Min { get; }                  // 数值范围（供屏幕键盘）
    public double Max { get; }

    private readonly ProgramStep _s;
    private readonly string _key;

    /// <param name="range">数值范围覆盖（FieldDef 范围与全局限值的交集，见 ProgramLimits.FieldRange）；null = 用 FieldDef 自身范围。</param>
    public StepField(ProgramStep s, FieldDef f, (double Min, double Max)? range = null)
    {
        _s = s; _key = f.Key;
        Label = f.Label; Unit = f.Unit; Kind = f.Kind;
        Options = f.Options ?? Array.Empty<string>();
        (Min, Max) = range ?? (f.Min, f.Max);
    }

    /// <summary>当前数值（屏幕键盘读取初值用）。</summary>
    public double Number => _s.Num(_key);

    /// <summary>
    /// 编辑闸门（ProgramViewModel 注入为 Ops.TryEditProgram 的结果）：false = 被拒（程序执行中 / 通道运行中 / 远程控制中），
    /// 不写入并让控件回到原值。null = 不设闸门。
    /// </summary>
    public Func<bool>? Gate { get; init; }

    /// <summary>屏幕键盘确认后写回数值（调用方已经过 TryEditProgram，这里不再过闸门）。</summary>
    public void SetNumber(double v)
    {
        _s.SetNum(_key, v);
        OnPropertyChanged(nameof(Value));
    }

    public string Value
    {
        get => IsNum ? StepDef.N(_s.Num(_key)) : _s.Str(_key);
        set
        {
            if (Gate is { } gate && !gate())
            {
                // 下拉框双向绑定：同一轮里通知可能被覆盖，延后一拍再通知一次，控件回到原值
                OnPropertyChanged();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Value)));
                return;
            }
            if (IsNum)
            {
                double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d);
                _s.SetNum(_key, d);
            }
            else _s.SetStr(_key, value ?? "");
            OnPropertyChanged();
        }
    }
}
