using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Models;

/// <summary>程序步骤类型，对应 HTML STEP_DEFS 的 key。</summary>
public enum StepType { Purge, Heat, Press, Stir, Wait, React, End }

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
                Type = StepType.Purge, Name = "吹扫", Chip = "PURGE", Brush = B("#7eb6ee"),
                Icon = G("M3 12h18 M9 6l-6 6 6 6 M21 6v12"),
                Help = "每次循环：充气→卸压。10 次循环可让 RV 内残留气体含量 < 0.1%。",
                Fields = new()
                {
                    new FieldDef{ Key="gas", Label="气体", Kind="sel", Options=new[]{"惰性气体","气体 A","气体 B"} },
                    new FieldDef{ Key="cycles", Label="循环次数", Kind="num", Unit="次", Default=10.0, Min=1, Max=50 },
                },
                DetailFn = s => $"{s.Str("gas","惰性气体")} × {N(s.Num("cycles",10))} 次",
            },
            [StepType.Heat] = new StepDef
            {
                Type = StepType.Heat, Name = "升温", Chip = "HEAT", Brush = B("#f0a830"),
                Icon = G("M12 22a7 7 0 0 0 7-7c0-2-1-3.9-3-5.5s-3.5-4-4-6.5c-.5 2.5-2 4.9-4 6.5s-3 3.5-3 5.5a7 7 0 0 0 7 7z"),
                Help = "升温建议：使用「等待平衡」给予 15-25 分钟，确保溶液温度达到设定值。",
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
                Type = StepType.Press, Name = "升压", Chip = "PRESS", Brush = B("#7eb6ee"),
                Icon = G("M12 2v20 M5 9l7-7 7 7"),
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
                Type = StepType.Stir, Name = "搅拌", Chip = "STIR", Brush = B("#d4a8ff"),
                Icon = G("M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z M12 2v4 M12 18v4 M4.93 4.93l2.83 2.83 M16.24 16.24l2.83 2.83 M2 12h4 M18 12h4"),
                Help = "搅拌速度变更不会立即生效，需要约 50 秒过渡。",
                Fields = new()
                {
                    new FieldDef{ Key="rpm", Label="转速", Kind="num", Unit="rpm", Default=1000.0, Min=0, Max=2000 },
                },
                DetailFn = s => $"{N(s.Num("rpm",1000))} rpm",
            },
            [StepType.Wait] = new StepDef
            {
                Type = StepType.Wait, Name = "等待", Chip = "WAIT", Brush = B("#a4a4b0"),
                Icon = G("M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 7v5l3 2"),
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
                Type = StepType.React, Name = "反应", Chip = "REACT", Brush = B("#a3e635"),
                Icon = G("M9 12l2 2 4-4 M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z"),
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
                Type = StepType.End, Name = "结束", Chip = "END", Brush = B("#e0394c"),
                Icon = G("M6 6h12v12H6z"),
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

    public double Num(string k, double def = 0)
        => _p.TryGetValue(k, out var v)
            ? (v is double d ? d : (double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dp) ? dp : def))
            : def;

    public string Str(string k, string def = "")
        => _p.TryGetValue(k, out var v) && v is string s ? s : def;

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
        StepType.Purge => 5,
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

    public StepField(ProgramStep s, FieldDef f)
    {
        _s = s; _key = f.Key;
        Label = f.Label; Unit = f.Unit; Kind = f.Kind;
        Options = f.Options ?? Array.Empty<string>();
        Min = f.Min; Max = f.Max;
    }

    /// <summary>当前数值（屏幕键盘读取初值用）。</summary>
    public double Number => _s.Num(_key);

    /// <summary>屏幕键盘确认后写回数值。</summary>
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
