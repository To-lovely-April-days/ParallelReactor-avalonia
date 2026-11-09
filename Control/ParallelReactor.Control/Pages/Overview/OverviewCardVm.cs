using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
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
/// 总览的一张反应釜卡片（样稿 overview.js cardHTML / reading / targets / auxHTML / progHTML + css/overview.css）。
/// 统一语法：标题「RV 16/600 · 状态点 + 状态字（不省略）」· 线描釜体 · 温度 26/600（紧凑 24）· 压力 20/600（紧凑 18）· 目标 15 灰 ·
/// PR4 宽卡多一行「搅拌 · 加热」· 底部「程序名 · 分段进度条 · 剩 x min」（第 k/n 步由分段条表示，完整文字在悬停提示里）。
/// <para>卡片按自身内容宽（容器 ovc）去掉次要部分：<see cref="ApplyCard"/> 收 Core 的 <see cref="OverviewCard"/>（目标单位 → 目标 → 釜体缩小 → 程序名 → 釜体；
/// 紧凑卡窄时状态字换到第二行、进度条与文字分两行）；读数字号按 data-len 阶梯（只有长读数才缩：<see cref="OverviewCard.Temp"/> / <see cref="OverviewCard.Pressure"/>）。
/// 目标「放得下才显示」、进度条每段 &lt; 6 px 时剩余时间让位 —— 这两条要量字宽，在视图里（OvReadRow / OvProgRow）。</para>
/// 读数 / 状态来自 <see cref="R"/>（ReactorVm，就地更新），这里只加总览自己的文字与取舍。单击 = 选中，双击 = 打开反应釜页签，右键 = 反应釜菜单（视图）。
/// </summary>
public sealed partial class OverviewCardVm : ObservableObject
{
    private OverviewCard? _card;
    private IReadOnlyList<StepSeg>? _segs;

    public OverviewCardVm(OverviewSectionVm section, ReactorVm r)
    {
        Section = section;
        R = r;
        SelectCommand = new RelayCommand(() => Section.Page.Shell.Select(Section.K, R.N));
        OpenCommand = new RelayCommand(() => Section.Page.Shell.Navigate(Route.Reactor(Section.K, R.N)));
    }

    public OverviewSectionVm Section { get; }
    public ReactorVm R { get; }
    public DeviceVm D => Section.D;
    public int N => R.N;

    public ICommand SelectCommand { get; }
    public ICommand OpenCommand { get; }

    /// <summary>反应釜菜单（右键，同资源管理器 / 时间轴：Shell/MenuModel.ReactorItems）。</summary>
    public IReadOnlyList<MenuEntry> ContextItems() => MenuModel.ReactorItems(Section.Page.Shell, D, R);

    // —— 种类（型号决定，overview.js cardKind） ——
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _isWide;

    // —— 状态 ——
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isOff;
    /// <summary>悬停提示（样稿 cardTip）：「1号机 · RV1 · 反应中 · 单击选中 · 双击打开」/ 断线「… · 离线（断线前 反应中）· 数据停在 … · …」。</summary>
    [ObservableProperty] private string _tip = "";

    // —— 卡片按内容宽的取舍（Core OverviewCard） ——
    [ObservableProperty] private bool _showVessel = true;
    [ObservableProperty] private double _vesselWidth = 36;
    [ObservableProperty] private double _vesselHeight = 56;
    [ObservableProperty] private double _bodyGap = 12;
    /// <summary>釜体右边到读数的间距（overview.css .overview-cbody gap：12；窄一排卡 10；紧凑卡 8）。</summary>
    [ObservableProperty] private Thickness _vesselMargin = new(0, 0, 12, 0);
    [ObservableProperty] private bool _headWrap;
    [ObservableProperty] private bool _progWrap;
    [ObservableProperty] private bool _showProgName = true;

    // —— 读数（data-len 阶梯：字号、行高、单位） ——
    [ObservableProperty] private double _tempSize = 26;
    [ObservableProperty] private double _tempLineHeight = 32;
    [ObservableProperty] private bool _tempUnit = true;
    [ObservableProperty] private double _pressSize = 20;
    [ObservableProperty] private double _pressLineHeight = 26;
    [ObservableProperty] private bool _pressUnit = true;
    /// <summary>温度行右侧的目标：「SP 120 ℃」「→ 120 ℃」（卡片 ≤ 239 px 时不带单位；紧凑卡与窄一排卡不写）；没有为 ""。</summary>
    [ObservableProperty] private string _targetT = "";
    /// <summary>压力行右侧的目标：「→ 0.350 MPa」。</summary>
    [ObservableProperty] private string _targetP = "";

    // —— PR4 宽卡第三行「搅拌 · 加热」 ——
    [ObservableProperty] private string _stirValue = Fmt.Dash;
    [ObservableProperty] private bool _stirUnit;
    /// <summary>值是「关」（400 t-3）。</summary>
    [ObservableProperty] private bool _stirMuted;
    [ObservableProperty] private string _heatLabel = "加热";
    [ObservableProperty] private string _heatValue = Fmt.Dash;
    [ObservableProperty] private bool _heatUnit;
    [ObservableProperty] private bool _heatMuted;
    [ObservableProperty] private bool _stirNa;
    [ObservableProperty] private bool _heatNa;
    [ObservableProperty] private double _auxSize = 18;
    /// <summary>数字的行高（WebKit：18 px × 1.45 → 26，16 px → 23）。</summary>
    [ObservableProperty] private double _auxLineHeight = 26;
    /// <summary>标签右边：间距 4 + 外边距 2（收紧时 0）。</summary>
    [ObservableProperty] private Thickness _auxLabelMargin = new(0, 0, 6, 0);
    /// <summary>第二项（加热 / 冷却水）的标签：左边是两项之间的间距 16（收紧时 10）。</summary>
    [ObservableProperty] private Thickness _auxSecondLabelMargin = new(16, 0, 6, 0);
    /// <summary>4 位转速 + 3 位加热且卡片 ≤ 199 px：第一项去掉「rpm」（overview.css:220）。</summary>
    [ObservableProperty] private bool _rpmUnitShown = true;
    /// <summary>写「rpm」：搅拌在转、且没有被收紧掉。</summary>
    [ObservableProperty] private bool _stirUnitShown;

    // —— 程序行 ——
    [ObservableProperty] private bool _hasProgram;
    /// <summary>没有程序：「无程序」（连上过）/「—」。</summary>
    [ObservableProperty] private string _noneText = "无程序";
    [ObservableProperty] private string _progName = "";
    [ObservableProperty] private IReadOnlyList<StepSeg>? _segments;
    /// <summary>当前段在运行（呼吸）：运行中、在线、不在等进气。</summary>
    [ObservableProperty] private bool _barRunning;
    /// <summary>程序行右侧的短文字：「剩 42 min」/「共 4 步」/「报警暂停」/「第 3/4 步」…</summary>
    [ObservableProperty] private string _progShort = "";
    /// <summary>短文字是运行中的剩余时间（t-2）。</summary>
    [ObservableProperty] private bool _progRun;
    /// <summary>短文字是「报警暂停」（t-1 600）。</summary>
    [ObservableProperty] private bool _progHeld;
    /// <summary>程序行的悬停提示（程序名 · 第 3/4 步 · 剩 42 min · 全程剩 1 h 32 min …）。</summary>
    [ObservableProperty] private string? _progTip;

    // ———————————————————— 刷新 ————————————————————

    internal void Refresh()
    {
        var d = D;
        var r = R;
        var kind = Section.Kind;
        IsCompact = kind == CardKind.Compact;
        IsWide = kind == CardKind.Wide;
        bool on = !d.IsOffline;
        IsOff = !on;
        Tip = CardTip(d, r, on);
        RefreshTargets();
        RefreshAux();
        RefreshProgram(on);
        ApplyFonts();
    }

    /// <summary>Core 的卡片取舍（总览宽度 → 这个分区的卡片内容宽）。</summary>
    internal void ApplyCard(OverviewCard card)
    {
        _card = card;
        ShowVessel = card.Vessel;
        VesselWidth = card.VesselWidth;
        VesselHeight = card.VesselHeight;
        BodyGap = card.BodyGap;
        VesselMargin = new Thickness(0, 0, card.BodyGap, 0);
        HeadWrap = card.HeadWrap;
        ProgWrap = card.ProgramWrap;
        ShowProgName = card.ProgramName;
        RefreshTargets();
        RefreshAux();
        ApplyFonts();
    }

    /// <summary>读数字号（data-len 阶梯）：长读数（5 个字符）在窄紧凑卡上逐级缩小、最后去单位；PR4 宽卡第三行同理（7 位）。</summary>
    private void ApplyFonts()
    {
        var card = _card ?? OverviewLayout.Card(Section.Kind, 400);
        var t = card.Temp(ReadingLength.Of(R.TempText));
        TempSize = t.Size;
        TempLineHeight = t.LineHeight;
        TempUnit = t.Unit;
        var p = card.Pressure(ReadingLength.Of(R.PressText));
        PressSize = p.Size;
        PressLineHeight = p.LineHeight;
        PressUnit = p.Unit;
        if (IsWide)
        {
            var a = card.Aux(AuxLen());
            AuxSize = a.ValueFont;
            AuxLineHeight = a.ValueFont >= 18 ? 26 : 23;
            AuxLabelMargin = new Thickness(0, 0, a.LabelMargin + 4, 0);
            AuxSecondLabelMargin = new Thickness(a.Gap, 0, a.LabelMargin + 4, 0);
            RpmUnitShown = a.RpmUnit;
            StirUnitShown = StirUnit && a.RpmUnit;
        }
    }

    /// <summary>
    /// 目标（样稿 targets）：升温 / 降温「→ 120 ℃」、升压 / 等待「→ 0.350 MPa」、反应 / 报警「SP 120 ℃」；按反应釜自己的状态；
    /// 卡片 ≤ 239 px 不带单位，紧凑卡和 ≤ 199 px 的一排卡不写（Core OverviewCard.Target / TargetUnit）。从未连上的设备不写。
    /// </summary>
    private void RefreshTargets()
    {
        var r = R;
        var card = _card;
        bool show = card?.Target ?? !IsCompact;
        bool unit = card?.TargetUnit ?? true;
        string t = "", p = "";
        if (show && r.Installed && D.EverOnline)
        {
            switch (r.Own)
            {
                case StateKey.Heat or StateKey.Cool when r.SpC is double sp:
                    t = "→ " + Round(sp) + (unit ? " ℃" : "");
                    break;
                case StateKey.Press or StateKey.Wait when r.PTargetMPa is double pt:
                    p = "→ " + PressureDisplay.Value(D.Model, pt) + (unit ? " " + PressureDisplay.Unit : "");
                    break;
                case StateKey.React or StateKey.Alarm when r.SpC is double sp2:
                    t = "SP " + Round(sp2) + (unit ? " ℃" : "");
                    break;
            }
        }
        TargetT = t;
        TargetP = p;
    }

    /// <summary>
    /// PR4 宽卡第三行（样稿 auxHTML）：搅拌（每釜独立：本釜开关；共用：整机开关）rpm / 「关」/ 「—」· 加热 % / 降温时「冷却水 开」。
    /// </summary>
    private void RefreshAux()
    {
        if (!IsWide) return;
        var r = R;
        var d = D;
        double? rpm = r.Rpm;
        double? heat = r.HeaterPct;
        bool stirOn = d.Model.StirMode == StirMode.Independent ? r.Extras?.StirOn ?? (rpm ?? 0) > 0 : d.Stir?.On == true;
        bool cooling = r.Cooling;
        StirNa = rpm is null;
        StirMuted = rpm is null || !stirOn;
        StirValue = rpm is null ? Fmt.Dash : stirOn ? Fmt.Rpm(rpm) : "关";
        StirUnit = rpm is not null && stirOn;
        HeatLabel = cooling ? "冷却水" : "加热";
        HeatNa = heat is null;
        HeatMuted = heat is null;
        HeatValue = heat is null ? Fmt.Dash : cooling ? "开" : Fmt.Pct(heat);
        HeatUnit = heat is not null && !cooling;
    }

    /// <summary>第三行的 data-len：两个数字的总位数（1500 rpm + 100 % → 7）；「关」/「开」/「—」各算 1。</summary>
    private int AuxLen()
    {
        int a = !StirNa && StirUnit ? StirValue.Length : 1;
        int b = !HeatNa && HeatUnit ? HeatValue.Length : 1;
        return a + b;
    }

    /// <summary>程序行（样稿 progHTML / progShort / stepBar）。</summary>
    private void RefreshProgram(bool online)
    {
        var d = D;
        var prog = R.Program;
        var pr = prog?.Progress;
        if (prog?.Record is not { } rec || pr is null)
        {
            if (R.Hmi is { StepCount: > 0 } hmi) { HmiProgram(hmi, online); return; }
            HasProgram = false;
            NoneText = d.EverOnline ? "无程序" : Fmt.Dash;
            ProgName = "";
            ProgShort = "";
            ProgRun = false;
            ProgHeld = false;
            ProgTip = null;
            BarRunning = false;
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
        // 程序行右侧的短文字（样稿 progShort）
        string text;
        bool isRun = false, held = false;
        if (pr.Status == ProgramStatus.Done) text = "共 " + I(pr.N) + " 步";
        else if (pr.Status == ProgramStatus.Loaded) text = "待运行";
        else if (pr.Status == ProgramStatus.Aborted) text = "已中止";
        else if (pr.Status == ProgramStatus.Failed) { text = "报警中止"; held = true; }   // 真机：报警中止该通道的程序（§3.5）
        else if (pr.Offline) text = "第 " + I(pr.K) + "/" + I(pr.N) + " 步";
        else if (pr.Status == ProgramStatus.Held) { text = "报警暂停"; held = true; }
        else if (pr.WaitGas) text = "第 " + I(pr.K) + "/" + I(pr.N) + " 步";
        else { text = pr.RemainShort; isRun = true; }
        ProgShort = text;
        ProgRun = isRun;
        ProgHeld = held;
        // 悬停提示（样稿 progHTML 的 data-tip）
        ProgTip = pr.Name + " · " + Progress.Text(pr)
            + (pr.Status == ProgramStatus.Running && !pr.Offline && !pr.WaitGas ? " · 全程剩 " + Fmt.Minutes(pr.TotalRemainMin) : "")
            + (pr.WaitGas ? " · 等待" + pr.GasName + "进气（触屏开阀）" : "");
    }

    /// <summary>
    /// 真机上本机台账（M5）对不上的程序（PORT-PLAN §3.1：没有程序名和步骤，HMI 只给第 k/n 步和消息）：程序名写「RVn 程序」，分段条按 k/n 画
    /// （当前步不知道进度，画最少的 8 %），右侧不写剩余时间 ——「第 k/n 步」/ 结束「共 n 步」/「已中止」/「报警中止」；悬停「RVn 程序 · 第 k/n 步 · 消息」。
    /// </summary>
    private void HmiProgram(Services.HmiProgram h, bool online)
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
        ProgName = R.Rv + " 程序";
        var segs = StepSegs.Update(_segs, bar);
        if (!ReferenceEquals(segs, _segs)) { _segs = segs; Segments = segs; }
        BarRunning = h.Running && online && !h.WaitingGas;
        string kn = I(k) + "/" + I(n);
        ProgShort = done ? "共 " + I(n) + " 步" : aborted ? "已中止" : failed ? "报警中止" : "第 " + kn + " 步";
        ProgRun = false;
        ProgHeld = failed;
        string state = done ? "已结束 · 共 " + I(n) + " 步" : aborted ? "已中止 · 第 " + kn + " 步" : failed ? "报警中止 · 第 " + kn + " 步" : "第 " + kn + " 步";
        ProgTip = ProgName + " · " + state + (string.IsNullOrWhiteSpace(h.Message) ? "" : " · " + h.Message);
    }

    internal static string CardTip(DeviceVm d, ReactorVm r, bool online)
    {
        var name = d.Alias + " · RV" + I(r.N);
        string st = !d.EverOnline ? "尚未连接" : online ? r.OwnWord : "离线（断线前 " + r.OwnWord + "）";
        return name + " · " + st + (online || !d.EverOnline ? "" : " · " + d.DataText) + " · 单击选中 · 双击打开";
    }

    /// <summary>JS Math.round（目标温度取整）。</summary>
    private static string Round(double v) => JsNumber.ToString(JsNumber.Round(v));

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);
}
