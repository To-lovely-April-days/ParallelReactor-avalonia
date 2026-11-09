using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 设备页的一张反应釜卡片（样稿 device.js cardHTML / cardHead / readsTP / kvRow / progBlock + css/device.css）：与总览同一套卡片语法、大一号 ——
/// 标题「RV 16/600 · 状态（点 + 字，不省略）」· 线描釜体 · 温度 30/600（主读数）+ 设定值 · 压力 20/600 + 目标 · 程序名 + 第 k/n 步 + 分段进度条 + 剩余时间；
/// HT-PR4 宽卡多三行「搅拌 / 加热 / 耗气」（右侧说明放得下才显示）。
/// <para>
/// 这里只给文字与标记；卡片内部的排法（标题换行、釜体在左 / 在上 / 不画、读数字号阶梯、设定值 / 目标同一行 / 单独一行 / 放不下整行不显示、
/// 宽卡按高度去掉次要行、程序块）由视图的 DvCard 按卡片自己的尺寸用 Core 的 <see cref="DeviceLayout.Card"/> 算（<see cref="Spec"/> = 网格给的取舍）。
/// </para>
/// 读数 / 状态来自 <see cref="R"/>（ReactorVm，就地更新）。单击 = 选中，双击 / Enter = 打开反应釜页，空格 = 选中，右键 = 反应釜菜单（视图）。
/// </summary>
public sealed partial class DeviceCardVm : ObservableObject
{
    private IReadOnlyList<StepSeg>? _segs;

    public DeviceCardVm(DevicePageVm page, ReactorVm r)
    {
        Page = page;
        R = r;
        SelectCommand = new RelayCommand(() => Page.Shell.Select(Page.K, R.N));
        OpenCommand = new RelayCommand(() => Page.Shell.Navigate(Route.Reactor(Page.K, R.N)));
    }

    public DevicePageVm Page { get; }
    public ReactorVm R { get; }
    public int N => R.N;

    public ICommand SelectCommand { get; }
    public ICommand OpenCommand { get; }

    /// <summary>反应釜菜单（右键，同资源管理器 / 时间轴 / 总览：Shell/MenuModel.ReactorItems）。</summary>
    public IReadOnlyList<MenuEntry> ContextItems() => Page.D is { } d ? MenuModel.ReactorItems(Page.Shell, d, R) : Array.Empty<MenuEntry>();

    /// <summary>网格给的取舍（种类、窄紧凑卡、8 路窄卡、面板放大）。</summary>
    [ObservableProperty] private DvCardSpec _spec = DvCardSpec.Default;

    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _isWide;
    [ObservableProperty] private bool _isSelected;
    /// <summary>设备断线（读数 t-3、釜体与进度条去色 + 0.8 不透明）。</summary>
    [ObservableProperty] private bool _isOff;
    /// <summary>悬停提示（device.js cardAttrs）：「1号机 · RV1 · 反应中 · 加氢-标准 第 3/4 步 · 剩 42 min\n双击打开反应釜页 · 右键更多操作」。</summary>
    [ObservableProperty] private string _tip = "";
    /// <summary>紧凑卡画简化釜体（面板放大后卡片够大，画完整釜体）。</summary>
    [ObservableProperty] private bool _simpleVessel;

    // —— 读数 ——
    /// <summary>温度读数的字符数（data-len：只有 5 位读数「199.9」在窄读数列里才缩字号）；没有读数为 0。</summary>
    [ObservableProperty] private int _tempLen;
    [ObservableProperty] private int _pressLen;
    /// <summary>温度旁的设定值：升温 / 降温「→ 120 ℃」，反应 / 保持「SP 120 ℃」；没有为 ""。</summary>
    [ObservableProperty] private string _tSub = "";
    /// <summary>压力旁的目标：升压中 / 等待中「→ 0.350 MPa」，等待进气「等待进气」；没有为 ""。</summary>
    [ObservableProperty] private string _pSub = "";
    /// <summary>升压中 / 等待中（显示的状态；断线时是「离线」）：设定值 / 目标放不下时先藏设定值（目标留到最后，device.js fitSubs）。</summary>
    [ObservableProperty] private bool _pressFirst;

    // —— 宽卡（HT-PR4）的次要行 ——
    [ObservableProperty] private string _stirValue = Fmt.Dash;
    [ObservableProperty] private bool _stirNa = true;
    [ObservableProperty] private int _stirLen;
    /// <summary>「本釜」/「本釜 · 停」/「共用」；从未连上为 ""。</summary>
    [ObservableProperty] private string _stirNote = "";
    [ObservableProperty] private string _stirTip = "";
    [ObservableProperty] private string _heatValue = Fmt.Dash;
    [ObservableProperty] private bool _heatNa = true;
    [ObservableProperty] private int _heatLen;
    /// <summary>「SP 150 ℃」/「→ 150 ℃」/「冷却 → 40 ℃」/「冷却」；没有为 ""。</summary>
    [ObservableProperty] private string _heatNote = "";
    [ObservableProperty] private string _heatTip = "";
    [ObservableProperty] private string _gasValue = Fmt.Dash;
    [ObservableProperty] private bool _gasNa = true;
    [ObservableProperty] private int _gasLen;
    [ObservableProperty] private string _gasTip = "";

    // —— 程序块 ——
    [ObservableProperty] private bool _hasProgram;
    /// <summary>没有程序：「无程序」（连上过）/「—」（从未连上：说明只写在面板顶部一次）。</summary>
    [ObservableProperty] private string _noneText = "无程序";
    [ObservableProperty] private string _progName = "";
    /// <summary>程序名行右侧：「第 3/4 步」/「已结束」/「待运行」。</summary>
    [ObservableProperty] private string _knText = "";
    /// <summary>进度条行右侧：「剩 42 min」/「等待进气」/「报警暂停」/「已中止」/「共 4 步」/「已下发」/「离线」。</summary>
    [ObservableProperty] private string _remText = "";
    [ObservableProperty] private bool _remRun;
    [ObservableProperty] private bool _remHeld;
    [ObservableProperty] private IReadOnlyList<StepSeg>? _segments;
    [ObservableProperty] private bool _barRunning;
    /// <summary>进度条行的悬停提示：「加氢-标准 · 第 3/4 步 · 剩 42 min」。</summary>
    [ObservableProperty] private string? _barTip;
    /// <summary>程序名行写的字：程序名 / 没有程序时「无程序」「—」（t-4）。</summary>
    [ObservableProperty] private string _nameText = "";
    /// <summary>进度条行右侧写的字：剩余时间等 / 没有程序时「无程序」「—」（t-4）。</summary>
    [ObservableProperty] private string _remShown = "";

    // ———————————————————— 刷新 ————————————————————

    internal void Refresh()
    {
        if (Page.D is not { } d) return;
        var r = R;
        bool on = !d.IsOffline;
        IsOff = !on;
        IsCompact = Spec.Kind == CardKind.Compact || DeviceLayout.CardKindOf(d.Model) == CardKind.Compact;
        IsWide = DeviceLayout.CardKindOf(d.Model) == CardKind.Wide;
        SimpleVessel = IsCompact && !Spec.Maximized;
        TempLen = r.TempNa ? 0 : r.TempText.Length;
        PressLen = r.PressNa ? 0 : r.PressText.Length;
        // 宽卡（HT-PR4）的设定值写在「加热」行（device.js readsTP 的 noSp），温度旁不写
        var ts = TSubOf(d, r);
        TSub = IsWide ? "" : ts;
        PSub = PSubOf(d, r);
        // 样稿 fitSubs 看卡片的状态类 st-press / st-wait：断线的设备是 st-offline（先藏目标），所以看显示的状态（R.Key），不看最后的状态（R.Own）
        PressFirst = r.Key is StateKey.Press or StateKey.Wait;
        if (IsWide) RefreshKv(d, r, on, ts);
        RefreshProgram(d, r, on);
        NameText = HasProgram ? ProgName : NoneText;
        RemShown = HasProgram ? RemText : NoneText;
        Tip = TipOf(d, r, on);
    }

    partial void OnSpecChanged(DvCardSpec value)
    {
        IsCompact = value.Kind == CardKind.Compact;
        SimpleVessel = IsCompact && !value.Maximized;
    }

    /// <summary>温度旁（device.js tSub）：从未连上、没有设定值、或不在加热 / 冷却 / 运行时为 ""。</summary>
    internal static string TSubOf(DeviceVm d, ReactorVm r)
    {
        if (!d.EverOnline || r.SpC is not double sp || !(r.Heating || r.Cooling || StateKeys.IsRunning(r.Own))) return "";
        return (r.Own is StateKey.Heat or StateKey.Cool ? "→ " : "SP ") + JsNumber.ToString(JsNumber.Round(sp)) + " ℃";
    }

    /// <summary>压力旁（device.js pSub）：等待进气（演示：进气口关闭；真机：排队等共用气路「等待气路」）/ 升压中、等待中的目标。</summary>
    internal static string PSubOf(DeviceVm d, ReactorVm r)
    {
        if (r.Program?.Progress is { WaitGas: true } pr) return pr.WaitWord;
        if (r.Program == null && r.Hmi is { Running: true, WaitingGas: true }) return StateMap.WaitGasWord;
        if (r.Own is StateKey.Press or StateKey.Wait && r.PTargetMPa is double t) return "→ " + PressureDisplay.Value(d.Model, t) + " " + PressureDisplay.Unit;
        return "";
    }

    /// <summary>宽卡的三行（device.js cardHTML kind === 'wide'）：搅拌 rpm（本釜 / 本釜 · 停 / 共用）、加热 %（设定值；降温时「冷却 → 40 ℃」）、耗气 mmol。</summary>
    private void RefreshKv(DeviceVm d, ReactorVm r, bool on, string ts)
    {
        string? rpm = r.Rpm is double v ? JsNumber.ToString(JsNumber.Round(v)) : null;
        string? heat = r.HeaterPct is double h ? JsNumber.ToString(JsNumber.Round(h)) : null;
        string? gas = r.Extras?.Mmol is double mm ? Fmt.Mmol(mm) : null;
        string stir = !d.EverOnline ? "" : d.Model.StirMode == StirMode.Independent ? (r.Extras?.StirOn == true ? "本釜" : "本釜 · 停") : "共用";
        string heatSub = r.Cooling && on ? "冷却" + (ts.Length > 0 ? " " + (ts.StartsWith("SP ", StringComparison.Ordinal) ? "→ " + ts[3..] : ts) : "") : ts;
        StirValue = rpm ?? Fmt.Dash;
        StirNa = rpm == null;
        StirLen = rpm?.Length ?? 0;
        StirNote = stir;
        StirTip = KvTip("搅拌", rpm, "rpm", stir);
        HeatValue = heat ?? Fmt.Dash;
        HeatNa = heat == null;
        HeatLen = heat?.Length ?? 0;
        HeatNote = heatSub;
        HeatTip = KvTip("加热", heat, "%", heatSub);
        GasValue = gas ?? Fmt.Dash;
        GasNa = gas == null;
        GasLen = gas?.Length ?? 0;
        GasTip = KvTip("耗气", gas, "mmol", "");
    }

    /// <summary>次要读数行的悬停提示（device.js kvRow）：「搅拌 800 rpm · 本釜」/「耗气 —」。</summary>
    private static string KvTip(string k, string? v, string unit, string sub) => k + " " + (v == null ? Fmt.Dash : v + " " + unit) + (sub.Length > 0 ? " · " + sub : "");

    /// <summary>程序块（device.js progBlock，逐字照样稿）。</summary>
    private void RefreshProgram(DeviceVm d, ReactorVm r, bool online)
    {
        var prog = r.Program;
        var pr = prog?.Progress;
        if (prog?.Record is not { } rec || pr is null)
        {
            if (r.Hmi is { StepCount: > 0 } hmi) { HmiProgram(r, hmi, online); return; }
            HasProgram = false;
            NoneText = d.EverOnline ? "无程序" : Fmt.Dash;
            ProgName = "";
            KnText = "";
            RemText = "";
            RemRun = RemHeld = false;
            BarRunning = false;
            BarTip = null;
            if (_segs != null) { _segs = null; Segments = null; }
            return;
        }
        HasProgram = true;
        ProgName = pr.Name;
        var bar = Progress.Bar(rec, pr, online);
        var segs = StepSegs.Update(_segs, bar);
        if (!ReferenceEquals(segs, _segs)) { _segs = segs; Segments = segs; }
        bool run = false;
        foreach (var s in bar) if (s.Pulse) run = true;
        BarRunning = run;
        KnText = pr.Status == ProgramStatus.Done ? "已结束" : pr.Status == ProgramStatus.Loaded ? "待运行" : "第 " + I(pr.K) + "/" + I(pr.N) + " 步";
        RemText = pr.Offline ? "离线"
            : pr.Status == ProgramStatus.Running ? (pr.WaitGas ? pr.WaitWord : pr.RemainShort)
            : pr.Status == ProgramStatus.Held ? "报警暂停"
            : pr.Status == ProgramStatus.Aborted ? "已中止"
            : pr.Status == ProgramStatus.Failed ? "报警中止"
            : pr.Status == ProgramStatus.Done ? "共 " + I(pr.N) + " 步"
            : "已下发";
        RemRun = !pr.Offline && pr.Status == ProgramStatus.Running;
        RemHeld = !pr.Offline && pr.Status is ProgramStatus.Held or ProgramStatus.Failed;
        BarTip = pr.Name + " · " + Progress.Text(pr);
    }

    /// <summary>
    /// 真机上本机台账（M5）对不上的程序（PORT-PLAN §3.1：HMI 只给第 k/n 步和消息，没有程序名、步骤、剩余时间）：程序名写「RVn 程序」，
    /// 程序名行右侧「第 k/n 步」（结束「已结束」），分段条按 k/n 画（当前步画最少的 8 %）；进度条行右侧不写剩余时间 —— 结束「共 n 步」/「已中止」/「报警中止」，
    /// 运行中空着（进度条占满）；悬停「RVn 程序 · 第 k/n 步 · 消息」。写法同总览卡片（OverviewCardVm.HmiProgram）。
    /// </summary>
    private void HmiProgram(ReactorVm r, Services.HmiProgram h, bool online)
    {
        int n = h.StepCount, k = Math.Clamp(h.StepIndex + 1, 1, n);
        bool done = h.State == "completed", aborted = h.State == "aborted", failed = h.State == "failed";
        var bar = new List<StepBarSegment>(n);
        for (int i = 0; i < n; i++)
        {
            var st = done || i < k - 1 ? StepBarState.Done
                : i == k - 1 ? (aborted || failed ? StepBarState.Abort : h.Running ? StepBarState.Cur : StepBarState.Pending)
                : StepBarState.Pending;
            bar.Add(new StepBarSegment(st, st == StepBarState.Done || st == StepBarState.Abort ? 100 : st == StepBarState.Cur ? 8 : 0,
                st == StepBarState.Cur && online && !h.WaitingGas, ""));
        }
        HasProgram = true;
        ProgName = r.Rv + " 程序";
        var segs = StepSegs.Update(_segs, bar);
        if (!ReferenceEquals(segs, _segs)) { _segs = segs; Segments = segs; }
        BarRunning = h.Running && online && !h.WaitingGas;
        string kn = I(k) + "/" + I(n);
        KnText = done ? "已结束" : "第 " + kn + " 步";
        RemText = !online ? "离线" : done ? "共 " + I(n) + " 步" : aborted ? "已中止" : failed ? "报警中止" : "";
        RemRun = false;
        RemHeld = online && failed;
        string state = done ? "已结束 · 共 " + I(n) + " 步" : aborted ? "已中止 · 第 " + kn + " 步" : failed ? "报警中止 · 第 " + kn + " 步" : "第 " + kn + " 步";
        BarTip = ProgName + " · " + state + (string.IsNullOrWhiteSpace(h.Message) ? "" : " · " + h.Message);
    }

    /// <summary>悬停提示（device.js cardAttrs）。</summary>
    private static string TipOf(DeviceVm d, ReactorVm r, bool online)
    {
        string name = d.Alias + " · RV" + I(r.N);
        string st = online ? r.OwnWord : d.EverOnline ? "离线（断线前 " + r.OwnWord + "）" : "未连接";
        var pr = r.Program?.Progress;
        string prog = pr != null ? " · " + pr.Name + " " + Progress.Text(pr)
            : r.Hmi is { StepCount: > 0 } h ? " · " + r.Rv + " 程序 第 " + I(Math.Clamp(h.StepIndex + 1, 1, h.StepCount)) + "/" + I(h.StepCount) + " 步" : "";
        return name + " · " + st + prog + (online ? "" : " · " + d.DataText) + "\n双击打开反应釜页 · 右键更多操作";
    }

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 一段连续未安装的位置（样稿 device.js unCol：140 px 虚线窄列 —— 淡显虚线釜体 + 「RV5–RV8」+「未安装」）。紧凑卡按 8 列对齐时跨它自己的列数。
/// </summary>
public sealed partial class DeviceUntileVm : ObservableObject
{
    public DeviceUntileVm(DevicePageVm page, int from, int to)
    {
        Page = page;
        From = from;
        To = to;
        Label = from == to ? "RV" + from.ToString(CultureInfo.InvariantCulture)
            : "RV" + from.ToString(CultureInfo.InvariantCulture) + "–RV" + to.ToString(CultureInfo.InvariantCulture);
    }

    public DevicePageVm Page { get; }
    public int From { get; }
    public int To { get; }
    /// <summary>'RV5–RV8'。</summary>
    public string Label { get; }

    /// <summary>悬停提示：'1号机 · RV5–RV8 未安装（HT-PR8 · 现场）· 不参与运行'。</summary>
    [ObservableProperty] private string _tip = "";

    internal void Refresh()
    {
        if (Page.D is { } d) Tip = d.Alias + " · " + Label + " 未安装（" + d.ModelLabel + "）· 不参与运行";
    }
}
