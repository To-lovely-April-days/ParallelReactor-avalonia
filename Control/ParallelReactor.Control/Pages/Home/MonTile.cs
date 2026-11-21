using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>通道小块上的报警标记（样稿 ind：报警红色实心圆 !、提醒琥珀三角 !；没有报警时画状态点）。</summary>
public enum TileMark { None, Warn, Err }

/// <summary>
/// 气路图 / 卡片 / 棒图三种视图的文字规则（融合版 F4，样稿 leftTxt / vesselCard / barView / gasView）：纯函数，单元测试直接调。
/// </summary>
public static class MonText
{
    /// <summary>
    /// 本步剩余的短写（样稿 leftTxt）：报警「已暂停」、急停「急停」、已停止、已结束、已就绪「待启动」、停用「已停用」，运行中「剩 27 min」（没有程序进度时「—」）。
    /// </summary>
    public static string Left(StateKey own, bool estop, ProgressInfo? pr)
    {
        if (estop && own is not (StateKey.Idle or StateKey.Uninstalled)) return "急停";
        return own switch
        {
            StateKey.Alarm => "已暂停",
            StateKey.Stopped => "已停止",
            StateKey.Done => "已结束",
            StateKey.Ready => "待启动",
            StateKey.Idle or StateKey.Uninstalled => "已停用",
            _ => pr is { } p && RunRowText.Running(own) ? "剩 " + p.StepRemainMin.ToString(CultureInfo.InvariantCulture) + " min" : Fmt.Dash,
        };
    }

    /// <summary>卡片底行（样稿 vpt）：「3/4 · 剩 27 min」「—/4 · 待启动」「4/4 · 已结束」；不知道有几步时只写剩余。</summary>
    public static string StepLine(int? step, int? count, string left) =>
        count is int n ? (step is int k ? k.ToString(CultureInfo.InvariantCulture) : Fmt.Dash) + "/" + n.ToString(CultureInfo.InvariantCulture) + " · " + left : left;

    /// <summary>值 / 量程，夹在 0–1；没有值为 NaN。</summary>
    public static double Frac(double? v, double max) => v is double x && max > 0 ? Math.Clamp(x / max, 0, 1) : double.NaN;

    /// <summary>
    /// 等宽步骤小条（卡片 .vpg / 棒图 .ss）：已完成 / 当前（进度至少 8 %，报警红）/ 未到；程序已结束全部算完成；没有程序时 <paramref name="count"/> 段全是未到。
    /// </summary>
    public static IReadOnlyList<StepSeg> Strip(int count, int? cur, double within, bool done, bool err)
    {
        var list = new StepSeg[Math.Max(0, count)];
        for (int i = 0; i < list.Length; i++)
        {
            int no = i + 1;
            var s = done || (cur is int c && no < c) ? SegmentState.Done : cur == no ? SegmentState.Cur : SegmentState.Next;
            list[i] = new StepSeg(1, s, s == SegmentState.Cur ? Math.Clamp(within, 0, 1) : 0, s == SegmentState.Cur && err, "");
        }
        return list;
    }

    /// <summary>悬停提示（样稿 vesselCard / barView 的 tip）：「RV1 · 反应中 · 步骤 3/4 · 剩 27 min · 搅拌 400 rpm · 加热 45 %」。</summary>
    public static string Tip(string rv, string word, int? step, int? count, string left, double? rpm, double? heat) =>
        rv + " · " + word + " · 步骤 " + (step is int k ? k.ToString(CultureInfo.InvariantCulture) : Fmt.Dash) + "/" + (count is int n ? n.ToString(CultureInfo.InvariantCulture) : Fmt.Dash)
        + " · " + left + " · 搅拌 " + (rpm is double r ? Math.Round(r).ToString(CultureInfo.InvariantCulture) : Fmt.Dash) + " rpm · 加热 "
        + (heat is double h ? Math.Round(h).ToString(CultureInfo.InvariantCulture) : Fmt.Dash) + " %";

    /// <summary>共用总管下的说明（样稿 hdrl）：「共用气路总管 HDR-01 · 气体 A」/「… · 气源全关」—— 只写气路名，不写接的是什么气（由工艺决定）。</summary>
    public static string Header(string? openInlet) => "共用气路总管 HDR-01 · " + (openInlet is { Length: > 0 } g ? g : "气源全关");

    /// <summary>阀门的悬停提示（HMI v0.7 起点击直接开关，见 <see cref="ValveCtl"/>）：「进气阀 SV1 · 开 · 点击关闭」。</summary>
    public static string ValveTip(string name, bool open, string? reason = null) => ValveCtl.Tip(name, open, reason);
}

/// <summary>
/// 气路图 / 卡片 / 棒图里的一路（融合版 F4）。身份 = RV 号；每次刷新 <see cref="Apply"/> 就地更新（三种视图共用同一组对象）。
/// </summary>
public sealed partial class MonTileVm : ObservableObject
{
    public MonTileVm(int n)
    {
        N = n;
        Rv = "RV" + n.ToString(CultureInfo.InvariantCulture);
        Num = n.ToString(CultureInfo.InvariantCulture);
    }

    public int N { get; }
    public string Rv { get; }
    /// <summary>只写编号（1366 宽的 16 路棒图：样稿 .rvp 不显示）。</summary>
    public string Num { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private bool _isOff;
    [ObservableProperty] private bool _stale;
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private RunDot _dot;
    [ObservableProperty] private TileMark _mark;
    [ObservableProperty] private string? _markTip;
    [ObservableProperty] private string _tip = "";
    [ObservableProperty] private string _cardTip = "";

    // 读数
    [ObservableProperty] private string _tempText = Fmt.Dash;
    [ObservableProperty] private string _spLine = "";
    [ObservableProperty] private string _pressText = Fmt.Dash;
    [ObservableProperty] private bool _pressHot;
    [ObservableProperty] private string _gasText = Fmt.Dash;
    [ObservableProperty] private string _rpmText = Fmt.Dash;
    [ObservableProperty] private string _stepLine = "";
    [ObservableProperty] private IReadOnlyList<StepSeg> _segs = Array.Empty<StepSeg>();

    // 釜图
    [ObservableProperty] private double _heat;
    [ObservableProperty] private bool _cooling;
    [ObservableProperty] private bool _bubbles;
    [ObservableProperty] private double _tFrac = double.NaN;
    [ObservableProperty] private double _spFrac = double.NaN;
    [ObservableProperty] private double _pFrac;
    [ObservableProperty] private double _pspFrac = double.NaN;
    [ObservableProperty] private double _limFrac = double.NaN;

    // 气路
    [ObservableProperty] private bool _svOpen;
    [ObservableProperty] private bool _flow;
    [ObservableProperty] private string _svTip = "";
    public string SvLabel => "SV" + Num;
    public string CvTip => "单向阀 CV" + Num;

    /// <summary>
    /// 按反应釜视图模型更新。<paramref name="tmax"/> / <paramref name="pmax"/> = 量程；<paramref name="plim"/> = 压力报警上限；
    /// <paramref name="srcOn"/> = 有气源开着（进气阀开着的路才有气流 / 气泡）；<paramref name="estop"/> = 设备急停中；<paramref name="mark"/> = 这一路的报警标记。
    /// </summary>
    /// <param name="devSteps">设备上主要程序的步数（这一路没有程序时卡片照样写「—/4」、画空的步骤条，同样稿用设备的程序）。</param>
    public void Apply(ReactorVm r, double tmax, double pmax, double? plim, bool srcOn, bool estop, bool selected, bool stale, TileMark mark, string? markTip,
        int? devSteps = null, string? svReason = null)
    {
        var own = r.Own;
        IsSelected = selected;
        Stale = stale;
        IsErr = own == StateKey.Alarm;
        IsOff = own is StateKey.Idle or StateKey.Uninstalled;
        Word = RunRowText.Word(own, r.Cause, r.OwnWord);
        Dot = IsErr ? RunDot.Err : StateKeys.IsRunning(own) ? RunDot.Run : RunDot.Idle;
        Mark = mark;
        MarkTip = markTip;

        // 程序：步骤数 = 程序的步数；第几步只在运行 / 报警 / 已结束时写（同运行表）
        var rec = r.Program?.Record;
        var pr = r.Program?.Progress;
        int? recSteps = rec is { Steps.Count: > 0 } ? rec.Steps.Count : null;
        int? count = recSteps ?? devSteps;
        bool showStep = recSteps != null && own is not (StateKey.Idle or StateKey.Ready or StateKey.Uninstalled);
        bool done = rec?.Status == ProgramStatus.Done;
        int? step = showStep ? (done ? count : Math.Clamp(rec!.StepIndex, 0, count!.Value - 1) + 1) : null;
        string left = MonText.Left(own, estop, showStep ? pr : null);
        StepLine = MonText.StepLine(step, count, left);
        var segs = count is int nSteps ? MonText.Strip(nSteps, step, pr?.Within ?? 0, showStep && done, IsErr) : Array.Empty<StepSeg>();
        if (Segs.Count != segs.Count || !Segs.SequenceEqual(segs)) Segs = segs;
        double? rpm = IsOff ? null : r.Rpm;
        Tip = MonText.Tip(Rv, Word, step, count, left, rpm, IsOff ? null : r.HeaterPct);
        CardTip = Rv + " · " + Word + " · 点击打开控制";

        TempText = r.TempText;
        SpLine = "目标 " + (r.SpC is double sp ? sp.ToString("0.0", CultureInfo.InvariantCulture) : Fmt.Dash) + " °C";
        PressText = r.PressureMPa is double pv ? PressureDisplay.Fixed(pv, 2) : r.PressText;
        PressHot = IsErr;
        GasText = r.GasMmol is double g ? g.ToString("0.0", CultureInfo.InvariantCulture) : IsOff ? "0.0" : Fmt.Dash;
        RpmText = rpm is double rr ? Math.Round(rr).ToString(CultureInfo.InvariantCulture) : Fmt.Dash;

        bool live = !IsOff;
        Heat = live && r.Heating ? Math.Max(1, r.HeaterPct ?? 50) : 0;
        Cooling = live && r.Cooling;
        TFrac = MonText.Frac(live ? r.TempC : null, tmax);
        SpFrac = MonText.Frac(live ? r.SpC : null, tmax);
        PFrac = MonText.Frac(live ? r.PressureMPa : null, pmax) is var pf && double.IsFinite(pf) ? pf : 0;
        PspFrac = r.PTargetMPa is double ps && ps > 0 && live ? MonText.Frac(ps, pmax) : double.NaN;
        LimFrac = MonText.Frac(plim, pmax);

        SvOpen = r.Valve;
        Flow = r.Valve && srcOn && live;
        Bubbles = Flow;
        SvTip = MonText.ValveTip("进气阀 SV" + Num, r.Valve, svReason);

        // v0.8.1 活动覆盖（样稿 act.js axTile / axDot）：「泄漏测试」+ 品牌蓝点 +「平衡 · 剩 05:20」；吸气量写「—」；排队等气路只换状态字
        if (r.Act is { } act)
        {
            Word = act.Word;
            if (act.Kind == Fleet.ChannelActKind.Queue) { Dot = RunDot.Idle; StepLine = MonText.StepLine(step, count, "排队"); }
            else
            {
                Dot = act.Amber ? RunDot.Amb : RunDot.Brand;
                StepLine = act.Line;
                var aseg = RunRowVm.ActSegs(act.Segs);
                if (aseg.Count > 0 && (Segs.Count != aseg.Count || !Segs.SequenceEqual(aseg))) Segs = aseg;
                GasText = Fmt.Dash;
            }
            Tip = Rv + " · " + act.Word + (act.Line.Length > 0 ? " · " + act.Line : "");
            CardTip = Rv + " · " + act.Word + " · 点击打开控制";
        }
    }
}

/// <summary>气路图左侧的一路气源（样稿 .inr：名字 · 横向阀门 · 横管）。</summary>
public sealed partial class GasInletVm : ObservableObject
{
    public GasInletVm(string key) => Key = key;

    public string Key { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _tip = "";
}

/// <summary>
/// 气路图的整机部分（融合版 F4，样稿 gasView）：气源（惰性气体 / 气体 A / 气体 B）、汇流管、共用总管 HDR-01、排空 / 泄压阀。
/// 阀门（HMI v0.7）：点击直接开关（要控制权；关阀不要），不经维护模式（<see cref="ValveCtl"/>）。
/// </summary>
public sealed partial class GasNetVm : ObservableObject
{
    public ObservableCollection<GasInletVm> Inlets { get; } = new();

    /// <summary>有气源开着（汇流管有气流）。</summary>
    [ObservableProperty] private bool _srcOn;
    /// <summary>总管有气流（有气源开着，并且有一路进气阀或排空开着）。</summary>
    [ObservableProperty] private bool _headerFlow;
    [ObservableProperty] private bool _ventOn;
    [ObservableProperty] private string _ventTip = "";
    [ObservableProperty] private string _headerText = "";

    internal void Refresh(DeviceVm d)
    {
        var m = d.Model;
        var src = d.Source;
        int i = 0;
        string? open = null;
        foreach (var g in m.GasInlets)
        {
            var v = i < Inlets.Count && Inlets[i].Key == g.Key ? Inlets[i] : null;
            if (v == null) { v = new GasInletVm(g.Key); if (i < Inlets.Count) Inlets[i] = v; else Inlets.Add(v); }
            v.Name = g.Name;
            v.IsOn = src.GasOpen(g.Key);
            v.Tip = MonText.ValveTip(g.Name + " · 接什么气由工艺决定", v.IsOn, ValveCtl.GasReason(d, g.Key, v.IsOn));
            if (v.IsOn) open ??= g.Name;
            i++;
        }
        while (Inlets.Count > i) Inlets.RemoveAt(Inlets.Count - 1);
        VentOn = m.Vent is { } vl && src.GasOpen(vl.Key);
        VentTip = MonText.ValveTip("排空 / 泄压阀", VentOn, m.Vent is { } vk ? ValveCtl.GasReason(d, vk.Key, VentOn) : null);
        SrcOn = open != null;
        bool anySv = d.Reactors.Any(r => r.Installed && r.Valve && r.Own is not (StateKey.Idle or StateKey.Uninstalled));
        HeaderFlow = SrcOn && (anySv || VentOn);
        HeaderText = MonText.Header(open);
    }
}
