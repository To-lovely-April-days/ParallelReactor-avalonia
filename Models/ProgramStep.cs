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

/// <summary>
/// 步骤静态定义：名称 / 角标 / 帮助 / 字段 / 摘要取自纯逻辑表 <see cref="StepSpecs"/>（STEPS-SPEC §1，接口与校验共用），
/// 这里只补触屏用的图标与颜色。只有 v0.8 的九种步骤（<see cref="StepTypeCodes.All"/>）；旧码 purge / wait 读进来即转换，不在表里。
/// </summary>
public class StepDef
{
    public StepType Type;
    public StepSpec Spec = null!;
    public Geometry Icon = Geometry.Parse("M6 6h12v12H6z");
    public IBrush Brush = Brushes.Gray;

    public string Name => Spec.Name;
    public string Chip => Spec.Chip;
    public string Help => Spec.Help;
    public List<FieldDef> Fields => Spec.Fields;

    public static readonly Dictionary<StepType, StepDef> Registry = Build();

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Geometry G(string d) => Geometry.Parse(d);

    /// <summary>数字格式化：整数不带小数。</summary>
    public static string N(double v) => StepSpecs.N(v);

    private static StepDef D(StepType t, string brush, string icon) => new() { Type = t, Spec = StepSpecs.Of(t), Brush = B(brush), Icon = G(icon) };

    private static Dictionary<StepType, StepDef> Build() => new()
    {
        [StepType.Leak] = D(StepType.Leak, "#5f9aa0", "M12 2l8 3v6c0 5-3.5 9-8 11-4.5-2-8-6-8-11V5z M9 12l2 2 4-4"),
        [StepType.Exchange] = D(StepType.Exchange, "#6f93b8", "M9.6 4.6A2 2 0 1 1 11 8H2 M12.6 19.4A2 2 0 1 0 14 16H2 M17.5 8A2.5 2.5 0 1 1 19.5 12.5H2"),
        [StepType.Stir] = D(StepType.Stir, "#7e86a8", "M21 12a9 9 0 1 1-6.22-8.56 M21 3v6h-6"),
        [StepType.Heat] = D(StepType.Heat, "#c79450", "M14 14.76V3.5a2.5 2.5 0 0 0-5 0v11.26a4.5 4.5 0 1 0 5 0z"),
        [StepType.Press] = D(StepType.Press, "#5f87b5", "M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0"),
        [StepType.Hold] = D(StepType.Hold, "#6b7079", "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 6v6l4 2"),
        [StepType.React] = D(StepType.React, "#6f9f6a", "M8.5 2h7 M10 2v6.5L5.6 18a2 2 0 0 0 1.8 3h9.2a2 2 0 0 0 1.8-3L14 8.5V2 M7 15h10"),
        [StepType.Vent] = D(StepType.Vent, "#8a8f98", "M12 20V6 M6 12l6-6 6 6 M4 3h16"),
        [StepType.End] = D(StepType.End, "#b0555f", "M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z M4 22v-7"),
    };
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

    /// <summary>按字段默认值新建步骤（数值 = 默认值，选项 = 默认码；STEPS-SPEC §1）。</summary>
    public static ProgramStep New(StepType type)
    {
        var s = new ProgramStep(type);
        foreach (var kv in StepSpecs.Defaults(type)) s._p[kv.Key] = kv.Value;
        return s;
    }

    /// <summary>参数（只读视图：选项为英文码，压力为 psi）。</summary>
    public IReadOnlyDictionary<string, object> Params => _p;

    public ProgramStep With(string k, object v) { _p[k] = v; return this; }

    // 解析规则与下发快照共用（StepParams），二者不会漂移
    public double Num(string k, double def = 0) => StepParams.Num(_p, k, def);

    public string Str(string k, string def = "") => StepParams.Str(_p, k, def);

    public void SetNum(string k, double v) { _p[k] = v; RaiseChanged(); }
    public void SetStr(string k, string v) { _p[k] = v; RaiseChanged(); }

    /// <summary>只刷新摘要文字（压力单位变了：摘要里的压力按新单位显示，参数本身没变，不算修改）。</summary>
    public void RefreshDetail() => OnPropertyChanged(nameof(Detail));

    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(Detail));
        Changed?.Invoke();
    }

    /// <summary>静态定义（旧码 purge / wait 不在表里：它们读进来就已转换，正常不会出现）。</summary>
    public StepDef Def => StepDef.Registry.TryGetValue(Type, out var d) ? d : StepDef.Registry[StepType.End];
    public string Name => Def.Name;
    public string Chip => Def.Chip;
    public Geometry Icon => Def.Icon;
    public IBrush IconBrush => Def.Brush;
    public string Detail => StepSpecs.Detail(Type, _p, SegTuples());

    /// <summary>时长估算（STEPS-SPEC §5，与 PC 同一套）。</summary>
    public DurationEstimate Duration => StepDuration.Of(Type, _p, SegTuples());

    /// <summary>该步骤计入总时长的分钟数。</summary>
    public double DurationMin => Duration.Minutes;

    private List<(double Temp, double Rate, double Hold)> SegTuples() => Segments.Select(g => (g.Temp, g.Rate, g.Hold)).ToList();

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

/// <summary>
/// 检查器里一行可编辑字段（num / sel），双向绑定回 ProgramStep。
/// 选项字段：步骤里存英文码，下拉框显示中文 label（<see cref="FieldDef.Options"/>）。
/// 压力字段：步骤里存 psi，显示、屏幕键盘的初值与范围都按当前压力单位（<see cref="Units"/>），确认后换回 psi。
/// 显示条件（visibleWhen）由 <see cref="Shown"/> 给出：同一步骤的选项改了之后，ProgramViewModel 调 <see cref="RefreshShown"/>。
/// </summary>
public partial class StepField : ObservableObject
{
    public string Key => _f.Key;
    public string Label { get; }
    public string? Unit { get; }
    public string Kind { get; }                 // num | sel
    public string[] Options { get; }
    public bool IsNum => Kind == "num";
    public bool IsSel => Kind == "sel";
    /// <summary>压力类字段（存 psi，按压力单位显示）。</summary>
    public bool IsPressure => _f.Pressure;

    /// <summary>数值范围（供屏幕键盘；压力字段为显示单位）。每次读取重算：跨字段规则（放到 ≤ 充到 − 1、最短 ≤ 最长）跟着同一步骤的当前值。</summary>
    public double Min => DisplayRange().Min;
    public double Max => DisplayRange().Max;

    private readonly ProgramStep _s;
    private readonly FieldDef _f;
    private readonly string _key;
    private readonly Func<(double Min, double Max)>? _range;

    /// <param name="range">数值范围（psi / 原单位；FieldDef 范围与全局限值的交集，见 ProgramLimits.FieldRange）；null = 用 FieldDef 自身范围。</param>
    public StepField(ProgramStep s, FieldDef f, Func<(double Min, double Max)>? range = null)
    {
        _s = s; _f = f; _key = f.Key; _range = range;
        Label = f.Label; Kind = f.Kind;
        Unit = f.Pressure ? (f.Unit == "psi/h" ? $"{Units.PLabel}/h" : Units.PLabel) : f.Unit;
        Options = f.Options?.Select(o => o.Label).ToArray() ?? Array.Empty<string>();
    }

    private (double Min, double Max) DisplayRange()
    {
        var (lo, hi) = _range?.Invoke() ?? (_f.Min, _f.Max);
        if (_f.Pressure)
        {
            // 换成显示单位后按显示小数位向内取整：键盘能输入的最大 / 最小值换回 psi 后仍在范围内
            double k = Math.Pow(10, Units.PDecimals);
            lo = Math.Ceiling(Units.P(lo) * k - 1e-9) / k;
            hi = Math.Floor(Units.P(hi) * k + 1e-9) / k;
            if (hi < lo) hi = lo;
        }
        return (lo, hi);
    }

    /// <summary>当前数值（屏幕键盘读取初值用；压力字段为显示单位）。</summary>
    public double Number => _f.Pressure ? Math.Round(Units.P(_s.Num(_key)), Units.PDecimals) : _s.Num(_key);

    /// <summary>当前参数下是否显示（visibleWhen）。</summary>
    public bool Shown => StepSpecs.IsVisible(_s.Type, _f, _s.Params);

    public void RefreshShown() => OnPropertyChanged(nameof(Shown));

    /// <summary>
    /// 编辑闸门（ProgramViewModel 注入为 Ops.TryEditProgram 的结果）：false = 被拒（程序执行中 / 通道运行中 / 远程控制中），
    /// 不写入并让控件回到原值。null = 不设闸门。
    /// </summary>
    public Func<bool>? Gate { get; init; }

    /// <summary>屏幕键盘确认后写回数值（v 为显示单位；调用方已经过 TryEditProgram，这里不再过闸门）。</summary>
    public void SetNumber(double v)
    {
        double stored = _f.Pressure ? Math.Round(Units.ToPsi(v), 3) : v;
        if (_f.Integer) stored = Math.Round(stored);
        _s.SetNum(_key, stored);
        OnPropertyChanged(nameof(Value));
    }

    public string Value
    {
        get => IsNum
            ? (_f.Pressure ? Units.PValue(_s.Num(_key)) : StepDef.N(_s.Num(_key)))
            : _f.LabelOf(_s.Str(_key, _f.DefaultSel));
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
                SetNumber(d);
            }
            else _s.SetStr(_key, _f.CodeOf(value ?? "") ?? _f.DefaultSel);
            OnPropertyChanged();
        }
    }
}
