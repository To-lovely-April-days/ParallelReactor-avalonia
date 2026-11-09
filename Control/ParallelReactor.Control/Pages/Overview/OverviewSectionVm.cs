using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 总览里的一个设备分区（样稿 overview.js panelHTML / headHTML / noteHTML / bodyHTML，PrPanel 的分区头版本）。
/// <list type="bullet">
/// <item>分区头（一行安静的状态，overview.js:141）：别名 18/600 · 型号 14 t-3（放不下整个不显示，别名最后才省略）· 短控制权徽标（断线时连接徽标）·
///   「运行 4/4」· 红色「急停中」· 报警 / 提醒小块（只有一条错误报警时直接写「RV11 超压报警」）· 「记录中」；右侧「打开」· «⋯»（设备菜单 + 设备信息）· 放大。</item>
/// <item>说明条（overview.js noteHTML）：断线（重连倒计时 + 立即重连）/ 首次连接 / 急停，一条中性说明；从未连上的设备只有说明（is-only）。</item>
/// <item>卡片网格（overview.js bodyHTML）：<see cref="Tiles"/> = 反应釜卡片 + 合并的未安装块，顺序与分组同 Core 的 <see cref="OverviewLayout"/>；
///   位置 / 尺寸来自 <see cref="Layout"/>（总览宽度 → Core）。</item>
/// </list>
/// 单击分区 = 选中设备（检查器跟过去），双击空白 = 打开设备页，右键 = 设备菜单（视图）。全部就地更新。
/// </summary>
public sealed partial class OverviewSectionVm : ObservableObject
{
    private ModelProfile? _structModel;
    private bool _structEver;

    public OverviewSectionVm(OverviewPageVm page, DeviceVm device)
    {
        Page = page;
        D = device;
        OpenCommand = new RelayCommand(() => Page.Shell.Navigate(Route.Device(K)));
        SelectCommand = new RelayCommand(() => Page.Shell.Select(K));
        ChipCommand = new RelayCommand(() => Page.Shell.Select(K, ChipTarget));
        NoteCommand = new RelayCommand(() => _ = D.Actions.RunAsync(DeviceAction.Reconnect));
    }

    public OverviewPageVm Page { get; }
    public DeviceVm D { get; }
    public int K => D.K;

    /// <summary>卡片与未安装块（网格的子元素，顺序 = Core 版面的块顺序）。</summary>
    public ObservableCollection<object> Tiles { get; } = new();

    public ICommand OpenCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand ChipCommand { get; }
    /// <summary>说明条上的按钮（立即重连 / 立即重试 / 连接）。</summary>
    public ICommand NoteCommand { get; }

    // —— 分区头 ——
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _model = "";
    /// <summary>别名 / 型号的悬停提示（样稿 idTip：别名 · 型号 · SN · 地址）。</summary>
    [ObservableProperty] private string _nameTip = "";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private LeaseState _lease;
    [ObservableProperty] private string _leaseShort = "";
    [ObservableProperty] private string _leaseTip = "";
    [ObservableProperty] private ConnState _conn;
    [ObservableProperty] private string _connShort = "";
    [ObservableProperty] private string _connTip = "";
    /// <summary>连上过（「运行 k/n」只在连上过的设备上写）。</summary>
    [ObservableProperty] private bool _everOnline;
    /// <summary>「运行」/「断线前运行」/「断开前运行」。</summary>
    [ObservableProperty] private string _runWord = "运行";
    [ObservableProperty] private string _runCount = "0";
    [ObservableProperty] private string _runTotal = "/0";
    [ObservableProperty] private string _runTip = "";
    [ObservableProperty] private bool _estop;
    [ObservableProperty] private string _estopText = "";
    [ObservableProperty] private string _estopTip = "";
    // 报警 / 提醒小块
    [ObservableProperty] private bool _hasChip;
    [ObservableProperty] private bool _chipErr;
    [ObservableProperty] private bool _chipWarn;
    [ObservableProperty] private bool _chipBoth;
    [ObservableProperty] private string _chipErrText = "";
    [ObservableProperty] private string _chipWarnText = "";
    [ObservableProperty] private string _chipTip = "";
    private int? ChipTarget { get; set; }
    // 记录中
    [ObservableProperty] private bool _session;
    [ObservableProperty] private string _sessionTip = "";
    /// <summary>写「记录中」：记录中、且工作区 &gt; 900（overview.css:261）。</summary>
    [ObservableProperty] private bool _sessionShown;
    /// <summary>「打开」的提示（打开1号机设备页）、«⋯» 的名字（1号机 · 更多操作）。</summary>
    [ObservableProperty] private string _openTip = "";
    [ObservableProperty] private string _moreTip = "";

    // —— 状态标记 ——
    /// <summary>选中的是这台设备本身（分区头 a-tint + 左侧 3 px a-tab）。</summary>
    [ObservableProperty] private bool _isSelected;
    /// <summary>断线（读数、程序变灰，样稿 .overview-dev.is-off）。</summary>
    [ObservableProperty] private bool _isOff;
    /// <summary>最后一个分区（没有底边，overview.css:18）。</summary>
    [ObservableProperty] private Thickness _borderThickness = new(0, 0, 0, 1);
    /// <summary>别的分区放大着：这个分区不显示。</summary>
    [ObservableProperty] private bool _isShown = true;
    /// <summary>这个分区放大着：铺满滚动区（高 = 滚动区高度），内容区自己滚动。</summary>
    [ObservableProperty] private bool _isMax;
    [ObservableProperty] private double _fillHeight = double.NaN;

    // —— 说明条 ——
    [ObservableProperty] private bool _hasNote;
    /// <summary>从未连上：只有说明条（下边距 12），不画卡片。</summary>
    [ObservableProperty] private bool _noteOnly;
    [ObservableProperty] private string _noteIcon = "wifioff";
    [ObservableProperty] private string _noteMain = "";
    [ObservableProperty] private string _noteSub = "";
    [ObservableProperty] private string? _noteLease;
    [ObservableProperty] private string? _noteLeaseTip;
    [ObservableProperty] private string? _noteButton;
    [ObservableProperty] private string? _noteReason;
    /// <summary>「控制权保留 N s」后面的按钮多 4 px（overview.css:67）。</summary>
    [ObservableProperty] private Thickness _noteButtonMargin;
    [ObservableProperty] private Thickness _noteMargin = new(16, 12, 16, 0);

    // —— 网格 ——
    /// <summary>Core 的分区版面（网格的列、块的位置、卡片按内容宽的取舍）；视图还没给尺寸时为 null。</summary>
    [ObservableProperty] private OverviewSection? _layout;
    [ObservableProperty] private bool _hasGrid;
    /// <summary>这个分区里有带进度条的卡片（紧凑卡程序行换行时的高度）。</summary>
    public bool AnyProgram { get; private set; }

    public CardKind Kind => OverviewLayout.CardKindOf(D.Model);

    /// <summary>«⋯» 菜单（同右键菜单：Shell/MenuModel.DeviceItems，总览只多一项「设备信息」，overview.js menuItems）。</summary>
    public IReadOnlyList<MenuEntry> MenuItems(Action showInfo) =>
        MenuModel.DeviceItems(Page.Shell, D, new[] { new MenuEntry { Label = "设备信息", Icon = "info", Run = showInfo } });

    /// <summary>右键菜单（与 «⋯» 同一份；右键没有锚点，不放「设备信息」，同样稿 ctxMenu → devMenuItems(d)）。</summary>
    public IReadOnlyList<MenuEntry> ContextItems() => MenuModel.DeviceItems(Page.Shell, D);

    // ———————————————————— 刷新 ————————————————————

    internal void Refresh()
    {
        var d = D;
        bool on = !d.IsOffline;
        Alias = d.Alias;
        Model = d.ModelLabel;
        NameTip = d.Alias + " · " + d.ModelLabel + " · SN " + d.Sn + " · " + d.Host + ":" + d.Port.ToString(CultureInfo.InvariantCulture);
        Online = on;
        Lease = d.Lease;
        LeaseShort = d.LeaseShort;
        LeaseTip = d.LeaseText;
        Conn = d.Conn;
        ConnShort = d.ConnShort;
        ConnTip = d.ConnText;
        EverOnline = d.EverOnline;
        // 用户自己断开的设备写「断开前运行」，断线（连接丢失）写「断线前运行」（overview.js:146）
        RunWord = on ? "运行" : d.ConnStatus == ConnStatus.Disconnected ? "断开前运行" : "断线前运行";
        RunCount = d.Running.ToString(CultureInfo.InvariantCulture);
        RunTotal = "/" + d.Installed.ToString(CultureInfo.InvariantCulture);
        RunTip = RunWord + " " + RunCount + " · 已安装 " + d.Installed.ToString(CultureInfo.InvariantCulture);
        Estop = d.EstopActive;
        EstopText = d.EstopText;
        EstopTip = d.EstopText + " · 复位只能在" + d.Alias + "触屏上操作";
        OpenTip = "打开" + d.Alias + "设备页";
        MoreTip = d.Alias + " · 更多操作";
        Session = d.SessionId != null;
        SessionTip = d.SessionId != null ? "会话 " + d.SessionId + " 记录中" : "";
        SessionShown = Session && !Page.HideSession;
        IsOff = !on;

        // 报警 / 提醒小块（Core.Alarms.AlarmTexts.Chip：急停中不数急停那条；一条错误报警写「RVn + 那个反应釜的状态字」）
        var chip = AlarmTexts.Chip(d.AlarmList, d.EstopActive, ch => d.Reactor(ch)?.OwnWord is { Length: > 0 } w ? w : "报警");
        HasChip = chip != null;
        ChipErr = chip?.HasErr == true;
        ChipWarn = chip?.HasWarn == true;
        ChipBoth = ChipErr && ChipWarn;
        ChipErrText = chip?.ErrText ?? "";
        ChipWarnText = chip?.WarnText ?? "";
        ChipTip = chip?.Tip ?? "";
        ChipTarget = chip?.TargetCh;

        RefreshNote();
        SyncTiles();
        foreach (var t in Tiles)
        {
            if (t is OverviewCardVm c) c.Refresh();
            else if (t is OverviewUntileVm u) u.Refresh();
        }
        AnyProgram = Tiles.OfType<OverviewCardVm>().Any(c => c.HasProgram);
    }

    /// <summary>说明条（overview.js noteHTML，逐字照样稿）。</summary>
    private void RefreshNote()
    {
        var d = D;
        var src = d.Source;
        var conn = src.Conn;
        bool on = conn.Online;
        string? lease = null, leaseTip = null, btn = null;
        string icon, main, sub;
        if (!on)
        {
            icon = "wifioff";
            sub = d.DataText;
            string rvs = d.Model.InstalledText;
            string Below(string what) => d.EverOnline ? "下方为" + what + "前的读数 · " + d.DataText : "尚未收到数据";
            if (d.IsFirst && conn.State == ConnStatus.Connecting)
            {
                icon = "link";
                main = "正在连接 " + d.Host + ":" + d.Port.ToString(CultureInfo.InvariantCulture) + "…";
                sub = "尚未收到数据 · 连上后这里显示 " + rvs;
            }
            else if (d.IsFirst)
            {
                main = "连接失败 · " + conn.RetryS.ToString(CultureInfo.InvariantCulture) + " s 后重试";
                sub = "尚未收到数据 · 请检查地址、端口与网络";
                btn = "立即重试";
            }
            else if (conn.State == ConnStatus.Connecting) { icon = "refresh"; main = "正在重连…"; sub = Below("断线"); }
            else if (conn.State == ConnStatus.Disconnected) { icon = "unplug"; main = "已断开 · 不会自动重连"; sub = Below("断开"); btn = "连接"; }
            else
            {
                main = conn.RetryS.ToString(CultureInfo.InvariantCulture) + " s 后自动重连"
                    + (conn.Attempts > 1 ? "（已试 " + (conn.Attempts - 1).ToString(CultureInfo.InvariantCulture) + " 次）" : "");
                sub = Below("断线");
                btn = "立即重连";
            }
            var lv = src.Lease;
            if (lv.State == LeaseStatus.Lost)
            {
                lease = "控制权保留 " + lv.GraceS.ToString(CultureInfo.InvariantCulture) + " s";
                leaseTip = "本机控制权保留 " + lv.GraceS.ToString(CultureInfo.InvariantCulture) + " s · 超时交回触屏";
            }
            HasNote = true;
        }
        else if (d.EstopActive)
        {
            icon = "estop";
            main = "加热、进气与搅拌已切断";
            sub = "复位只能在" + d.Alias + "触屏上操作 · 本机不能复位";
            HasNote = true;
        }
        else
        {
            icon = "info"; main = ""; sub = "";
            HasNote = false;
        }
        NoteIcon = icon;
        NoteMain = main;
        NoteSub = sub;
        NoteLease = lease;
        NoteLeaseTip = leaseTip;
        NoteButton = btn;
        NoteReason = btn == null ? null : Nz(d.Actions.Why(DeviceAction.Reconnect));
        NoteButtonMargin = new Thickness(lease != null ? 4 : 0, 0, 0, 0);
        NoteOnly = !d.EverOnline;
        NoteMargin = new Thickness(16, 12, 16, NoteOnly ? 12 : 0);
    }

    /// <summary>
    /// 网格的块（同 Core 的 <see cref="OverviewLayout.Section"/> 的块顺序与分组：紧凑卡的未安装段不跨过 8 列的边界）：型号或连上过变了才重排，
    /// 已有的卡片按 RV 号保留（R10）。从未连上的设备没有块。
    /// </summary>
    private void SyncTiles()
    {
        var model = D.Model;
        bool ever = D.EverOnline;
        if (ReferenceEquals(model, _structModel) && ever == _structEver) return;
        _structModel = model;
        _structEver = ever;
        var want = new List<object>();
        if (ever)
        {
            var sec = OverviewLayout.Section(model, 1200);
            foreach (var t in sec.Tiles)
            {
                if (t.Installed)
                {
                    var r = D.Reactor(t.From);
                    if (r == null) continue;
                    want.Add(Tiles.OfType<OverviewCardVm>().FirstOrDefault(c => ReferenceEquals(c.R, r)) ?? new OverviewCardVm(this, r));
                }
                else want.Add(Tiles.OfType<OverviewUntileVm>().FirstOrDefault(u => u.From == t.From && u.To == t.To) ?? new OverviewUntileVm(this, t.From, t.To));
            }
        }
        for (int i = Tiles.Count - 1; i >= 0; i--) if (!want.Contains(Tiles[i])) Tiles.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            int at = Tiles.IndexOf(want[i]);
            if (at < 0) Tiles.Insert(i, want[i]);
            else if (at != i) Tiles.Move(at, i);
        }
    }

    private (ModelProfile Model, double Width, bool Ever, bool Note, bool Last, bool AnyProgram) _layoutKey;
    private OverviewSection? _layoutCache;

    /// <summary>这个分区的 Core 版面（输入没变时返回上一次的同一个实例）。</summary>
    internal OverviewSection LayoutFor(double width, bool last)
    {
        var key = (D.Model, width, D.EverOnline, HasNote, last, AnyProgram);
        if (_layoutCache != null && key == _layoutKey) return _layoutCache;
        _layoutKey = key;
        return _layoutCache = OverviewLayout.Section(D.Model, width, D.EverOnline, HasNote, last, AnyProgram);
    }

    /// <summary>Core 的分区版面（总览宽度变了、数据变了之后）：网格、底边、卡片的取舍。版面实例没变时卡片只按新数据刷新读数字号。</summary>
    internal void ApplyLayout(OverviewSection sec)
    {
        Layout = sec;
        HasGrid = !sec.NeverConnected && Tiles.Count > 0;
        BorderThickness = new Thickness(0, 0, 0, sec.Last ? 0 : OverviewLayout.SectionBorder);
        foreach (var c in Tiles.OfType<OverviewCardVm>()) c.ApplyCard(sec.Card);
        FillHeight = IsMax ? Page.ScrollHeight : double.NaN;
        SessionShown = Session && !Page.HideSession;
    }

    internal void UpdateSelection(int k, int? n)
    {
        IsSelected = k == K && n == null;
        foreach (var c in Tiles.OfType<OverviewCardVm>()) c.IsSelected = k == K && n == c.N;
    }

    internal void UpdateMax(int? maxK)
    {
        IsMax = maxK == K;
        IsShown = maxK == null || maxK == K;
        FillHeight = IsMax ? Page.ScrollHeight : double.NaN;
    }

    // ———————————————————— 设备信息（«⋯» → 设备信息，overview.js infoPop） ————————————————————

    /// <summary>设备信息弹出框的键值行（连接 / 地址 / SN / 版本 / 通道 / 会话 / 断线时的数据）。打开时取一次。</summary>
    public IReadOnlyList<(string Key, string Value)> InfoRows()
    {
        var d = D;
        var rows = new List<(string, string)>
        {
            ("连接", d.ConnText),
            ("地址", d.Host + ":" + d.Port.ToString(CultureInfo.InvariantCulture)),
            ("SN", d.Sn.Length > 0 ? d.Sn : Core.Format.Fmt.Dash),
            ("版本", "HMI " + d.Source.HmiVersion + " · 接口 " + d.Source.ApiVersion),
            ("通道", d.Model.ChannelCount.ToString(CultureInfo.InvariantCulture) + " 路 · 已装 " + d.Model.InstalledText),
            ("会话", d.SessionId != null ? d.SessionId + " · 记录中" : "未记录"),
        };
        if (d.IsOffline) rows.Add(("数据", d.DataText));
        return rows;
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>
/// 一段连续未安装的位置（样稿 unTile：淡显虚线小釜体 +「RV5–RV8 / 未安装」，line-2 虚线块）。紧凑卡按 8 列对齐时跨它自己的列数。
/// </summary>
public sealed partial class OverviewUntileVm : ObservableObject
{
    public OverviewUntileVm(OverviewSectionVm section, int from, int to)
    {
        Section = section;
        From = from;
        To = to;
        Label = from == to ? "RV" + from.ToString(CultureInfo.InvariantCulture)
            : "RV" + from.ToString(CultureInfo.InvariantCulture) + "–RV" + to.ToString(CultureInfo.InvariantCulture);
    }

    public OverviewSectionVm Section { get; }
    public int From { get; }
    public int To { get; }
    /// <summary>'RV5–RV8'。</summary>
    public string Label { get; }

    /// <summary>悬停提示：'1号机 · RV5–RV8 未安装（HT-PR8 · 现场）· 不参与运行'。</summary>
    [ObservableProperty] private string _tip = "";

    internal void Refresh() => Tip = Section.D.Alias + " · " + Label + " 未安装（" + Section.D.ModelLabel + "）· 不参与运行";
}
