using System;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Records;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Pages.Records;

/// <summary>
/// 会话列表的一行（样稿 .rcl：分组头 .rcg「今天 / 昨天 / 更早」或会话 .rci）。会话行：名字 14/600 + 状态标签；
/// 第二行「1号机 · 平行合成程序 A · 7 路」；第三行「11-15 13:05 · 1 h 25 min」+「 · 会话号」（窄屏不写）。选中 = a-tint + 左 3 px 品牌蓝。
/// </summary>
public sealed partial class RecListItemVm : ObservableObject
{
    /// <summary>分组头。</summary>
    public RecListItemVm(string group)
    {
        IsGroup = true;
        Group = group;
        Id = "";
        PickCommand = new RelayCommand(() => { });
    }

    public RecListItemVm(string id, Action<string> pick)
    {
        Id = id;
        Group = "";
        PickCommand = new RelayCommand(() => pick(Id));
    }

    public bool IsGroup { get; }
    public string Group { get; }
    public string Id { get; }
    public ICommand PickCommand { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private BadgeTone _tone;
    [ObservableProperty] private bool _tagDot;
    [ObservableProperty] private string _line2 = "";
    [ObservableProperty] private string _line3 = "";
    [ObservableProperty] private string _idText = "";
    [ObservableProperty] private bool _isOn;

    /// <summary>就地更新（同一条会话）。</summary>
    public void Apply(RecSession s, string alias, DateTime now, bool showId = true)
    {
        Name = s.Name;
        var (t, tone) = RecTexts.Tag(s);
        Tag = t;
        Tone = tone switch { RecTagTone.Run => BadgeTone.Ok, RecTagTone.Warn => BadgeTone.Warn, _ => BadgeTone.None };
        TagDot = tone == RecTagTone.Run;
        var prog = RecTexts.ProgramText(s);
        Line2 = alias + (prog == "—" ? "" : " · " + prog) + " · " + s.Channels.Count.ToString(CultureInfo.InvariantCulture) + " 路";
        Line3 = RecTexts.Day(s.Start) + " " + RecTexts.Hm(s.Start) + " · " + RecTexts.Dur(s.Duration(now));
        IdText = showId ? " · " + s.Id : "";
    }
}

/// <summary>KPI 的语气：普通、提醒（RSD 大于 10 %：amb 底 + 琥珀字）、报警（报警数 &gt; 0 / 判定不通过：r-bg 底 + 红字）、通过（绿字）。</summary>
public enum RecKpiTone { None, Warn, Err, Ok }

/// <summary>一个指标（样稿 .kpi：名 13 t-3；值 22/600 + 单位 13 t-4；说明 13 t-3，矮屏不写）。</summary>
public sealed partial class RecKpiVm : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private RecKpiTone _tone;
    [ObservableProperty] private bool _hasSub;

    public bool IsWarn => Tone == RecKpiTone.Warn;
    public bool IsErr => Tone == RecKpiTone.Err;
    public bool IsOk => Tone == RecKpiTone.Ok;

    partial void OnToneChanged(RecKpiTone value)
    {
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsErr));
        OnPropertyChanged(nameof(IsOk));
    }

    public void Set(string key, string value, string unit = "", RecKpiTone tone = RecKpiTone.None, string sub = "")
    {
        Key = key; Value = value; Unit = unit; Tone = tone; Sub = sub; HasSub = sub.Length > 0;
    }
}

/// <summary>
/// 结果表的一行（样稿 .rres tr.rw）。吸气类：吸气 mmol（15/600）· 与平均相比（条 + 「+27%」）· 最大速率 · 到 90 %（「42 min」/「未到」）· 状态；
/// 没计入统计的一行写「已就绪 · 不计入统计」（t-none）。保压类：压降 psi/h · 换算 kPa/h · 判定标签（通过绿 / 不通过红）。点一行 = 选中（曲线加粗 + 面积），再点取消。
/// </summary>
public sealed partial class RecRowVm : ObservableObject
{
    public RecRowVm(int n, Action<int> pick, RecColumns cols)
    {
        N = n;
        Cols = cols;
        Rv = "RV" + n.ToString(CultureInfo.InvariantCulture);
        ChBrush = RunRowText.ChBrush(n);
        PickCommand = new RelayCommand(() => pick(N));
    }

    public int N { get; }
    public string Rv { get; }
    public IBrush ChBrush { get; }
    public ICommand PickCommand { get; }
    /// <summary>列宽（表头和各行共用一份，宽 / 窄屏、吸气类 / 保压类时就地换）。</summary>
    public RecColumns Cols { get; }

    [ObservableProperty] private bool _used = true;
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _gText = "";
    [ObservableProperty] private double _rel;
    [ObservableProperty] private string _relText = "";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string _t90Text = "";
    [ObservableProperty] private bool _hasT90;
    [ObservableProperty] private string _t90None = "—";
    [ObservableProperty] private string _t90Tip = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private RecTone _tone;
    [ObservableProperty] private string _offText = "";
    // 保压类
    [ObservableProperty] private string _dropText = "";
    [ObservableProperty] private string _kpaText = "";
    [ObservableProperty] private bool _ok = true;
    /// <summary>吸气类、计入统计的一行（显示吸气 / 相比 / 速率 / 到 90 % / 状态）。</summary>
    [ObservableProperty] private bool _showUptake = true;
    /// <summary>保压类、有数据的一行（压降 / 换算 / 判定）。</summary>
    [ObservableProperty] private bool _showHold;
    /// <summary>不计入统计的一行（「已就绪 · 不计入统计」）。</summary>
    [ObservableProperty] private bool _showOff;

    public bool IsWarn => Tone == RecTone.Warn;
    public bool IsErr => Tone == RecTone.Err;

    partial void OnToneChanged(RecTone value)
    {
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsErr));
    }

    public void Apply(RecRow r, RecStats st, double maxAbs, bool hold)
    {
        Used = r.Used;
        Result = r.Result;
        Tone = r.Tone;
        OffText = r.Result + " · 不计入统计";
        ShowUptake = r.Used && !hold;
        ShowHold = r.Used && hold;
        ShowOff = !r.Used;
        if (hold)
        {
            DropText = r.Used ? RecTexts.F2(r.Drop) : "—";
            KpaText = r.Used ? Core.Format.JsNumber.ToFixed(r.Drop * RecCompute.KPaPerPsi, 1) + " kPa/h" : "";
            Ok = r.Ok;
            return;
        }
        GText = RecTexts.F1(r.G);
        double rel = RecCompute.Rel(r.G, st.Mean);
        Rel = st.Mean != 0 && maxAbs > 0 ? rel / maxAbs : 0;
        RelText = st.Mean != 0 ? RecTexts.Pct(rel) : "";
        RateText = r.Rate > 0 ? RecTexts.F2(r.Rate) : "—";
        HasT90 = r.T90 is double;
        T90Text = r.T90 is double t ? Math.Round(t, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : "";
        T90None = r.Live ? "未到" : "—";
        T90Tip = r.Live ? "还在反应，没到终值" : "";
    }
}

/// <summary>
/// 结果表的列宽（样稿 .rres 在 1920 下的自动列宽：通道 72 · 吸气 92 · 与平均相比（其余）· 最大速率 98 · 到 90 % 85 · 状态 72；
/// 窄屏（&lt; 1600）62 · 84 · * · 76 · 72 · 64、格内左右 8（宽屏 10），「与平均相比」只写百分数）。保压类：通道 72 · 压降 * · 换算 * · 判定 136（窄屏 110）。
/// </summary>
public sealed partial class RecColumns : ObservableObject
{
    [ObservableProperty] private GridLength _c0 = new(72);
    [ObservableProperty] private GridLength _c1 = new(92);
    [ObservableProperty] private GridLength _c2 = new(1, GridUnitType.Star);
    [ObservableProperty] private GridLength _c3 = new(98);
    [ObservableProperty] private GridLength _c4 = new(85);
    [ObservableProperty] private GridLength _c5 = new(72);
    /// <summary>格内边距（左右 10，窄屏 8）。</summary>
    [ObservableProperty] private Avalonia.Thickness _pad = new(10, 0);
    [ObservableProperty] private bool _narrow;

    public void Set(bool narrow, bool hold)
    {
        Narrow = narrow;
        Pad = new Avalonia.Thickness(narrow ? 8 : 10, 0);
        if (hold)
        {
            C0 = new GridLength(narrow ? 62 : 72); C1 = new GridLength(1, GridUnitType.Star); C2 = new GridLength(1, GridUnitType.Star);
            C3 = new GridLength(narrow ? 110 : 136); C4 = new GridLength(0); C5 = new GridLength(0);
            return;
        }
        C0 = new GridLength(narrow ? 62 : 72); C1 = new GridLength(narrow ? 84 : 92); C2 = new GridLength(1, GridUnitType.Star);
        C3 = new GridLength(narrow ? 76 : 98); C4 = new GridLength(narrow ? 72 : 85); C5 = new GridLength(narrow ? 64 : 72);
    }
}

/// <summary>「标记与事件」的一行（样稿 .rcev：图标 16 · 时刻 64 宽 t-3 · 来源 52 宽 600 · 文字 t-2，行高 32、下边 line-faint）。</summary>
public sealed record RecEventVm(string Time, string Src, string Text, RecEventKind Kind)
{
    public string Icon => Kind switch { RecEventKind.Mark or RecEventKind.MarkErr => "flag", RecEventKind.Err => "err-f", RecEventKind.Warn => "warn-f", _ => "info-f" };

    /// <summary>图标颜色：标记 t-2、报警时刻的标记 / 报警 r-btn、提醒 amb、信息 t-3。</summary>
    public IBrush IconBrush => Kind switch
    {
        RecEventKind.Mark => Palette.T2Brush,
        RecEventKind.MarkErr or RecEventKind.Err => Palette.RBtnBrush,
        RecEventKind.Warn => Palette.AmbBrush,
        _ => Palette.T3Brush,
    };
}

/// <summary>「数据文件」的一行：文件名（图标 + 600）· 内容 t-3 · 大小 · 下载（演示数据带原因）。</summary>
public sealed record RecFileVm(string File, string What, string Size, ICommand Download, string? Reason);
