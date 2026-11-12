using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Vessel;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 反应釜页的「釜体 / 属性查看器」页签面板（样稿 js/pages/reactor.js tabsPanel / coView / stateActions / propCards / propsHTML / propsActions；
/// PORT-PLAN §5 M5「釜体页签与属性页签」）。一个反应釜页一个实例，身份不变；外框每次数据变化后由页面调 <see cref="Refresh"/> 就地更新。
/// <list type="bullet">
/// <item>页签（<see cref="Tab"/>：「釜体」/「属性查看器」，PTabs 的 Value，双向）：切换只换内容，面板本身（放大 / 关闭、键盘焦点）不变。</item>
/// <item>标题栏右侧：釜体页签 = 状态（点 + 字）—— 这个反应釜有错误报警时不显示（顶部报警条已经有了）；属性查看器页签 =「另有 N 项 ▾ / 收起 ▴」+「恢复全部属性」。</item>
/// <item>釜体：六个标注的内容（<see cref="Sv"/> … <see cref="Lv"/>，Controls/Vessel 的 <see cref="CalloutContent"/>，逐字照样稿 coView）+ 釜体的样子（<see cref="R"/>）；
///   版面（标注样式、位置、引线）由视图的 VesselStage 按面板尺寸用 <see cref="StageLayout"/> 算。</item>
/// <item>属性查看器：八张属性卡片（<see cref="Props"/>，身份不变、读数就地更新）；× 隐藏一张（后面的补上来）、「恢复全部」；
///   只显示完整的行（<see cref="SetPropsViewport"/>：列数 = (宽 − 28 + 14) ÷ (233 + 14)，行数 = (高 − 4) ÷ 83），其余收进「另有 N 项」，展开后面板内滚动。</item>
/// </list>
/// 文字的规则全在这里（纯视图模型，App.Tests 直接测）；真机没有的读数（液位、耗气量）写「—」，「触屏操作」照写。
/// </summary>
public sealed partial class ReactorVesselVm : ObservableObject
{
    public const string VesselTabValue = "vessel", PropsTabValue = "props";

    /// <summary>属性卡片：高 83（路径 24 + 主体 58 + 1 px 分隔线）、最窄 233（含 4 px 实时边）、列间距 14、左右内边距 16 + 12（样稿 PROP_H / PROP_MIN_W / PROP_GAP / PROP_PAD）。</summary>
    public const double PropH = 83, PropMinW = 233, PropGap = 14, PropPad = 28;

    private readonly ShellVm? _shell;
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    public ReactorVesselVm(ShellVm? shell, int k, int n)
    {
        _shell = shell;
        K = k;
        N = n;
        Props = new[]
        {
            new ReactorPropVm("pv", "thermo"), new ReactorPropVm("p", "gauge"), new ReactorPropVm("sp", "flame"), new ReactorPropVm("out", "flame"),
            new ReactorPropVm("stir", "fan"), new ReactorPropVm("gas", "droplet"), new ReactorPropVm("valve", "valve"), new ReactorPropVm("vol", "flask"),
        };
        RestoreCommand = new RelayCommand(Restore);
        ToggleMoreCommand = new RelayCommand(() => { PropsOpen = !PropsOpen; SyncShown(); });
        TabOptions = new[] { VesselTab, PropsTab };
        SyncShown();
    }

    public int K { get; }
    public int N { get; }

    /// <summary>这一页的反应釜（设备被移除后为 null）：釜体的样子（状态、加热、冷却、搅拌、液位）直接绑它。</summary>
    [ObservableProperty] private ReactorVm? _r;

    // ———————————————————— 页签 ————————————————————

    /// <summary>当前页签：「vessel」/「props」（PTabs 的 Value，双向）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsVesselTab), nameof(IsPropsTab), nameof(ShowBadge))] private string _tab = VesselTabValue;

    public bool IsVesselTab => Tab != PropsTabValue;
    public bool IsPropsTab => Tab == PropsTabValue;

    /// <summary>换了页签（视图把属性查看器的滚动回到顶部，样稿 body.scrollTop = 0）。</summary>
    public event EventHandler? TabSwitched;

    partial void OnTabChanged(string value)
    {
        if (value != VesselTabValue && value != PropsTabValue) { Tab = VesselTabValue; return; }
        TabSwitched?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>页签的两项（实例不变：悬停提示随型号就地改）。</summary>
    public SegOption VesselTab { get; } = new() { Value = VesselTabValue, Label = "釜体" };
    public SegOption PropsTab { get; } = new() { Value = PropsTabValue, Label = "属性查看器", Tip = "全部属性：温度、压力、加热、搅拌、耗气量、阀门、溶液体积" };

    /// <summary>PTabs 的 OptionsSource。</summary>
    public IReadOnlyList<SegOption> TabOptions { get; }

    // ———————————————————— 标题栏右侧 ————————————————————

    /// <summary>这个反应釜没有错误报警（有的话顶部报警条已经写了，标题栏不再重复一个红色「超压报警」，样稿 stateActions）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowBadge))] private bool _hasBadge;
    [ObservableProperty] private StateKey _badgeKey = StateKey.Offline;
    [ObservableProperty] private string _badgeText = "";
    [ObservableProperty] private bool _neverConnected;
    /// <summary>断线时「断线前：反应中」；在线时 null。</summary>
    [ObservableProperty] private string? _badgeTip;

    /// <summary>釜体页签的状态徽标（属性查看器页签时标题栏右侧是「另有 N 项」和「恢复全部属性」）。</summary>
    public bool ShowBadge => HasBadge && IsVesselTab;

    /// <summary>釜体（宽版式的「釜体」面板副标题）：「50 mL 釜」；悬停「50 mL 釜 · 溶液 ≤ 20 mL · 示例型号 · 参数为假设」。</summary>
    [ObservableProperty] private string _vesselKind = "";
    [ObservableProperty] private string _vesselTip = "";

    // ———————————————————— 釜体：六个标注 ————————————————————

    [ObservableProperty] private CalloutContent _sv = Blank("SV", "进气阀");
    [ObservableProperty] private CalloutContent _ht = Blank("HT", "加热");
    [ObservableProperty] private CalloutContent _tt = Blank("TT", "温度");
    [ObservableProperty] private CalloutContent _m = Blank("M", "搅拌");
    [ObservableProperty] private CalloutContent _pt = Blank("PT", "压力");
    [ObservableProperty] private CalloutContent _lv = Blank("LV", "液位");
    /// <summary>设备断线：读数停在断线前（标注的读数 t-3，样稿 .reactor-stage.is-stale）。</summary>
    [ObservableProperty] private bool _stale;

    private static CalloutContent Blank(string tag, string label) => new(tag, label, Fmt.Dash, "", Fmt.Dash);

    // ———————————————————— 属性查看器 ————————————————————

    /// <summary>八张属性卡片（顺序 = 重要程度，放不下时从后往前收进「另有 N 项」）。</summary>
    public IReadOnlyList<ReactorPropVm> Props { get; }

    /// <summary>显示着的卡片（没隐藏的、放得下的；展开时全部）。按身份增删，不重建。</summary>
    public ObservableCollection<ReactorPropVm> ShownProps { get; } = new();

    /// <summary>「另有 N 项」展开了（面板内滚动）。</summary>
    [ObservableProperty] private bool _propsOpen;
    /// <summary>属性卡片的列数 / 放得下的行数（视图给的尺寸算出来的）。</summary>
    [ObservableProperty] private int _propCols = 2;
    [ObservableProperty] private int _propRows = 3;
    /// <summary>有放不下的卡片（或展开了）：标题栏的「另有 N 项 ▾ / 收起 ▴」。</summary>
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _moreText = "";
    [ObservableProperty] private string _moreIcon = "down";

    public ICommand RestoreCommand { get; }
    public ICommand ToggleMoreCommand { get; }

    /// <summary>一屏放得下的卡片数（样稿 fit = max(列, 行 × 列)）。</summary>
    public int Fit => Math.Max(PropCols, PropRows * PropCols);

    /// <summary>
    /// 属性查看器内容区的尺寸（视图给的，设计 px）：列数 = clamp(⌊(宽 − 28 + 14) ÷ 247⌋, 1, 4)、行数 = max(1, ⌊(高 − 4) ÷ 83⌋)（样稿 measure）。
    /// 高度为 0（页签藏着）时不改。
    /// </summary>
    public void SetPropsViewport(double width, double height)
    {
        if (!(width > 0) || !(height > 0)) return;
        PropCols = ColumnsFor(width);
        PropRows = Math.Max(1, (int)Math.Floor((height - 4) / PropH));
        SyncShown();
    }

    /// <summary>属性卡片的列数（样稿 measure：clamp(⌊(宽 − 28 + 14) ÷ (233 + 14)⌋, 1, 4)）。视图的卡片网格用同一个公式。</summary>
    public static int ColumnsFor(double width) => Math.Clamp((int)Math.Floor((width - PropPad + PropGap) / (PropMinW + PropGap)), 1, 4);

    /// <summary>× 隐藏一张卡片（从网格里拿掉，后面的卡片补上来）。</summary>
    public void Hide(string key)
    {
        if (string.IsNullOrEmpty(key) || !_hidden.Add(key)) return;
        SyncShown();
    }

    /// <summary>恢复全部属性（toast「已恢复全部属性」）。</summary>
    public void Restore()
    {
        _hidden.Clear();
        SyncShown();
        _shell?.Toast("已恢复全部属性", ToastKind.Info);
    }

    /// <summary>
    /// 像新打开的页面（样稿 render：清空隐藏的卡片、收起「另有 N 项」）并切到 <paramref name="tab"/>（截图 / 核对用；样稿的页签是模块级状态，换页面不变）。
    /// </summary>
    public void ResetView(string tab = VesselTabValue)
    {
        Tab = tab == PropsTabValue ? PropsTabValue : VesselTabValue;
        _hidden.Clear();
        PropsOpen = false;
        SyncShown();
    }

    /// <summary>隐藏了的卡片的键（测试用）。</summary>
    public IReadOnlyCollection<string> Hidden => _hidden;

    /// <summary>按隐藏 / 放得下 / 展开同步 <see cref="ShownProps"/> 与「另有 N 项」（样稿 shownCards / propsHTML / propsActions）。</summary>
    private void SyncShown()
    {
        var cards = Props.Where(p => !_hidden.Contains(p.Key)).ToList();
        int fit = Fit;
        int show = PropsOpen ? cards.Count : Math.Min(cards.Count, fit);
        var want = cards.Take(show).ToList();
        for (int i = ShownProps.Count - 1; i >= 0; i--)
            if (!want.Contains(ShownProps[i])) ShownProps.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            int at = ShownProps.IndexOf(want[i]);
            if (at < 0) ShownProps.Insert(i, want[i]);
            else if (at != i) ShownProps.Move(at, i);
        }
        bool more = cards.Count > fit;
        HasMore = more;
        MoreText = more ? (PropsOpen ? "收起" : "另有 " + (cards.Count - fit).ToString(CultureInfo.InvariantCulture) + " 项") : "";
        MoreIcon = PropsOpen ? "up" : "down";
    }

    // ———————————————————— 刷新 ————————————————————

    /// <summary>从外框的数据刷新（页面的 Refresh 里调；设备没了时清空）。</summary>
    public void Refresh(DeviceVm? d)
    {
        var r = d?.Reactor(N);
        if (!ReferenceEquals(r, R)) R = r;
        if (d == null || r == null) return;
        var m = d.Model;
        bool on = !d.IsOffline;
        VesselKind = m.Vessel.Kind;
        VesselTip = m.Vessel.Kind + " · " + m.Vessel.Note + " · " + ModelProfiles.SampleNote;
        VesselTab.Tip = m.Vessel.Kind + " · 线描釜体与测点";

        // 标题栏：这个反应釜有错误报警时不写状态（顶部报警条已经有了）
        bool err = d.AlarmList.Any(a => a.Ch == N && a.IsErr);
        HasBadge = !err;
        BadgeKey = r.Key;
        BadgeText = r.Word;
        NeverConnected = !d.EverOnline;
        BadgeTip = on ? null : "断线前：" + r.OwnWord;

        var c = Callouts(d, r);
        Sv = c[VesselCallout.Sv];
        Ht = c[VesselCallout.Ht];
        Tt = c[VesselCallout.Tt];
        M = c[VesselCallout.M];
        Pt = c[VesselCallout.Pt];
        Lv = c[VesselCallout.Lv];
        Stale = !on;

        RefreshProps(d, r, on);
        SyncShown();
    }

    /// <summary>
    /// 六个标注的内容（样稿 coView，逐字照抄）：
    /// <list type="bullet">
    /// <item>SV 进气阀：开 / 关 + 气体名（开着时：当前步的气路，否则打开着的进气路）；说明行写耗气量（「耗气 1.82 mmol」，水滴图标，手形图标挪到读数旁），
    ///   没有耗气读数（真机）或在等进气时写「触屏操作」/「等待进气 · 触屏」；悬停另写排空阀。</item>
    /// <item>HT 加热 %：冷却水开 / 加热关闭 / 保温（|T − SP| ≤ 2）/ 全功率升温（≥ 99 %）/ 加热中；断线写「断线前保温」，不画火焰 / 雪花；从未连上「—」。</item>
    /// <item>TT 温度：SP（加热或冷却中且有设定值）/ 无设定值；实时边（在线）。</item>
    /// <item>M 搅拌 rpm：已停止 / 全釜共用 / 本釜独立；实时边（在线且在转）。</item>
    /// <item>PT 压力（主单位）+ 次单位；附加行：超压报警时「上限 3.000 MPa」（r-ink，去掉实时边），升压 / 等待中「目标 …」。
    ///   「超压」<b>只看这个反应釜有没有还没解除的超压错误报警</b>（不看通道状态：真机的超温报警不会在压力标注上写「上限」）。</item>
    /// <item>LV 液位 mL：触屏操作（真机没有液位：「—」）。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<VesselCallout, CalloutContent> Callouts(DeviceVm d, ReactorVm r)
    {
        var m = d.Model;
        int n = r.N;
        bool on = !d.IsOffline;
        double? T = r.TempC, P = r.PressureMPa, H = r.HeaterPct, V = r.Extras?.VolumeMl, rpm = r.Rpm, mm = r.Extras?.Mmol;
        var prog = r.Program?.Record;
        var pr = r.Program?.Progress;
        bool run = prog is { Status: ProgramStatus.Running or ProgramStatus.Held };
        var step = run ? prog!.CurrentStep : null;
        var src = d.Source;
        var openGas = m.GasInlets.FirstOrDefault(g => src.GasOpen(g.Key));
        string? gasKey = !string.IsNullOrEmpty(step?.Gas) ? step!.Gas : openGas?.Key;
        bool shared = m.StirMode == StirMode.Shared;
        bool stirOn = (rpm ?? 0) > 0;
        bool pAlarm = d.AlarmList.Any(a => a.Ch == n && a.IsErr && a.Active && a.Code == AlarmCodes.PressureHigh);
        bool pressing = (r.Own is StateKey.Press or StateKey.Wait) && r.PTargetMPa != null;
        double? sp = r.SpC;
        string htNow = r.Cooling ? "冷却水开" : !r.Heating ? "加热关闭"
            : sp != null && T != null && Math.Abs(T.Value - sp.Value) <= 2 ? "保温" : (H ?? 0) >= 99 ? "全功率升温" : "加热中";
        string htSub = on ? htNow : d.EverOnline ? "断线前" + htNow : Fmt.Dash;
        bool waitGas = pr is { WaitGas: true };
        string touchTip = !waitGas ? "触屏操作" : pr!.WaitWord == Progress.WaitGasWordDemo ? "等待进气 · 触屏" : pr.WaitWord;
        bool gasLine = mm != null && !waitGas;
        string vent = m.Vent is { } vl ? vl.Name.Split(' ')[0] + "阀 " + (src.GasOpen(vl.Key) ? "开" : "关") : "";
        string gasText = gasLine ? "耗气 " + Fmt.Mmol(mm) + " mmol" : "";
        string unitP = PressureDisplay.Unit;
        var o = new Dictionary<VesselCallout, CalloutContent>(6)
        {
            [VesselCallout.Sv] = new("SV" + n.ToString(CultureInfo.InvariantCulture), "进气阀", r.Valve ? "开" : "关",
                r.Valve && gasKey != null ? m.GasName(gasKey) : "",
                gasLine ? gasText : touchTip, Icon: gasLine ? "droplet" : "hand", Touch: true, HandVal: gasLine, TouchTip: touchTip,
                Tip: (gasLine ? gasText + " · " : "") + touchTip + (vent.Length > 0 ? " · " + vent : "")),
            [VesselCallout.Ht] = new("HT", "加热", Fmt.Pct(H), "%", htSub, Icon: on && r.Cooling ? "snow" : on && r.Heating ? "flame" : null),
            [VesselCallout.Tt] = new("TT", "温度", Fmt.Temp(T), "℃", (r.Heating || r.Cooling) && sp != null ? "SP " + Fmt.Temp(sp) + " ℃" : "无设定值",
                Main: true, Live: on),
            [VesselCallout.M] = new("M", "搅拌", Fmt.Rpm(rpm), "rpm", !stirOn ? "已停止" : shared ? "全釜共用" : "本釜独立", Live: on && stirOn),
            [VesselCallout.Pt] = new("PT", "压力", PressureDisplay.Value(m, P), unitP, P == null ? Fmt.Dash : PressureDisplay.SecondaryWithUnit(m, P),
                Extra: pAlarm ? "上限 " + PressureDisplay.Value(m, m.Limits.PressureMaxMPa) + " " + unitP
                    : pressing ? "目标 " + PressureDisplay.Value(m, r.PTargetMPa) + " " + unitP : "",
                Main: true, Live: on && !pAlarm, Err: on && pAlarm),
            [VesselCallout.Lv] = new("LV", "液位", Fmt.Ml(V), "mL", "触屏操作", Icon: "hand", Touch: true),
        };
        return o;
    }

    /// <summary>属性卡片（样稿 propCards，逐字照抄）：实时读数 → 设定 / 输出 → 搅拌 → 耗气 → 只读的阀门与体积；4 px 实时边只给在线的温度、压力、运转中的搅拌。</summary>
    private void RefreshProps(DeviceVm d, ReactorVm r, bool on)
    {
        var m = d.Model;
        string rv = "RV" + N.ToString(CultureInfo.InvariantCulture);
        string bas = "/" + d.Alias + "/" + rv;
        double? T = r.TempC, P = r.PressureMPa, H = r.HeaterPct, rpm = r.Rpm, mm = r.Extras?.Mmol, V = r.Extras?.VolumeMl;
        bool shared = m.StirMode == StirMode.Shared;
        bool vent = m.Vent is { } vl && d.Source.GasOpen(vl.Key);
        string sp = (r.Heating || r.Cooling) && r.SpC != null ? Fmt.Temp(r.SpC) : Fmt.Dash;
        Props[0].Apply(bas + "/模拟输入", "反应器温度", "温度 PV", Fmt.Temp(T), "℃", null, on, !on);
        Props[1].Apply(bas + "/模拟输入", "压力传感器", "压力", PressureDisplay.Value(m, P), PressureDisplay.Unit,
            P == null ? null : PressureDisplay.SecondaryWithUnit(m, P), on, !on);
        Props[2].Apply(bas + "/设备/加热器", "加热器", "温度设定值 SP", sp, "℃", null, false, !on);
        Props[3].Apply(bas + "/设备/加热器", "加热器", r.Cooling ? "加热输出 · 冷却中" : "加热输出", Fmt.Pct(H), "%", null, false, !on);
        Props[4].Apply(shared ? "/" + d.Alias + "/全局/搅拌电机" : bas + "/设备/搅拌电机", shared ? "搅拌（全局）" : "搅拌（本釜）",
            shared ? "RV1–RV" + m.ChannelCount.ToString(CultureInfo.InvariantCulture) + " 共用" : "转速", Fmt.Rpm(rpm), "rpm", null, on && (rpm ?? 0) > 0, !on);
        Props[5].Apply(bas + "/计算", "耗气量", "自进气起累计", Fmt.Mmol(mm), "mmol", null, false, !on);
        Props[6].Apply(bas + "/设备/阀门", "进气阀 SV" + N.ToString(CultureInfo.InvariantCulture),
            (m.Vent is { } v2 ? v2.Name.Split(' ')[0] : "排空") + "阀 " + (vent ? "开" : "关"), r.Valve ? "开" : "关", "", "触屏操作", false, !on);
        Props[7].Apply(bas + "/设备/反应釜", "溶液体积", "触屏操作", Fmt.Ml(V), "mL", "≤ " + JsNumber.ToString(m.Vessel.MaxFillMl) + " mL", false, !on);
    }
}

/// <summary>
/// 属性查看器的一张卡片（样稿 ui.propCard 的参数）：路径行、图标、名称 + 粗体说明、读数 + 单位（+ 第二单位 / 说明）、实时边、断线。
/// 身份 = <see cref="Key"/>（pv / p / sp / out / stir / gas / valve / vol），读数就地更新。
/// </summary>
public sealed partial class ReactorPropVm : ObservableObject
{
    public ReactorPropVm(string key, string icon)
    {
        Key = key;
        Icon = icon;
    }

    public string Key { get; }
    public string Icon { get; }

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _value = Fmt.Dash;
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string? _unit2;
    /// <summary>实时测量值：右侧 4 px g-edge。</summary>
    [ObservableProperty] private bool _edge;
    /// <summary>设备断线：读数 t-3。</summary>
    [ObservableProperty] private bool _stale;

    internal void Apply(string path, string title, string sub, string value, string unit, string? unit2, bool edge, bool stale)
    {
        Path = path;
        Title = title;
        Sub = sub;
        Value = value;
        Unit = unit;
        Unit2 = unit2;
        Edge = edge;
        Stale = stale;
    }

    public override string ToString() => Title + " " + Sub + " " + Value + " " + Unit + (string.IsNullOrEmpty(Unit2) ? "" : " · " + Unit2);
}
