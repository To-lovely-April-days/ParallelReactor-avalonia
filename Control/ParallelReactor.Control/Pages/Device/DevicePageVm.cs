using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using LayoutPage = ParallelReactor.Control.Core.Layout.DevicePage;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 设备页（样稿 js/pages/device.js + css/device.css，PORT-PLAN §2.2「设备页」、§5 M4）：两行铺满工作区，面板内容在面板里滚动、整页不滚动。
/// <list type="bullet">
/// <item>上排「反应釜」：标题「1号机 › 反应釜」+ 型号 · 釜 + 「运行 4/4」+ 控制权（断线时连接）徽标；断线 / 急停的说明条（「立即重连」在这里 —— 检查器不再放第二个）；
///   卡片网格（<see cref="Tiles"/>：Core 的 <see cref="DeviceLayout"/>：宽卡 / 一排竖卡 / 两排紧凑卡 + 合并的未安装窄列）；从未连上的设备只有一条说明。</item>
/// <item>下排：共用资源（<see cref="Res"/>）、设备信息（<see cref="Info"/>）、报警与事件（<see cref="Log"/>）；工作区窄于 1100（1600 / 1366）时后两块合成一个页签面板，
///   页签由 Core 的 <see cref="DeviceLogTabRule"/> 决定（打开页面 / 新的错误报警时切到「报警与事件」，用户点过页签之后不再替用户换；确认报警从不换页签）。</item>
/// </list>
/// 版面：视图把设备页根元素的尺寸（= 工作区，父元素给的尺寸，R4）交给 <see cref="SetViewport"/>，面板的放大 / 关闭交给 <see cref="SetPanel"/>；
/// 这里用 Core 的 <see cref="DeviceLayout.Compute"/> 算上排高度、卡片区滚动、网格与块的位置、下排面板的取舍。卡片内部的取舍（标题换行要量字宽）在视图的 DvCard 里，
/// 同样用 Core 的 <see cref="DeviceLayout.Card"/>。
/// <para>
/// 同一条报警屏幕上只有一个「确认」：「报警与事件」面板（或页签）可见时，这台设备未确认报警的「确认」在页面上（<see cref="OwnedAlarms"/>），
/// 检查器里只写「确认在「报警与事件」」；页面上有这台设备的报警与事件列表（<see cref="ShowsAlarmLog"/>）与断线说明条（<see cref="ShowsReconnect"/>）。
/// </para>
/// 数据：外框在每次数据变化后调 <see cref="Refresh"/>（就地更新文字，卡片与列表按身份增删，R10）。
/// </summary>
public sealed partial class DevicePageVm : PageVm
{
    private ModelProfile? _structModel;
    private bool _structEver;
    private DemoParams? _lastParams;
    private string _ownSig = "";

    public DevicePageVm(ShellVm shell, Route route) : base(route)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Res = new DeviceResVm(this);
        Info = new DeviceInfoVm(this);
        Log = new DeviceLogVm(this);
        ReconnectCommand = new RelayCommand(() => { if (D is { } d) _ = d.Actions.RunAsync(DeviceAction.Reconnect); });
        Shell.Selection.Changed += OnSelectionChanged;
    }

    public ShellVm Shell { get; }
    public int K => Route.K;

    /// <summary>这一页的设备（设备被移除后为 null：页签随即关掉）。</summary>
    public DeviceVm? D { get; private set; }

    public DeviceResVm Res { get; }
    public DeviceInfoVm Info { get; }
    public DeviceLogVm Log { get; }

    /// <summary>卡片（<see cref="DeviceCardVm"/>）与连续未安装的窄列（<see cref="DeviceUntileVm"/>），顺序 = Core 版面的块顺序。</summary>
    public ObservableCollection<object> Tiles { get; } = new();

    /// <summary>说明条上的「立即重连」/「连接」。</summary>
    public ICommand ReconnectCommand { get; }

    // —— 反应釜面板标题 ——
    [ObservableProperty] private string _crumb = "";
    [ObservableProperty] private string _crumbTip = "";
    /// <summary>'HT-PR8 · 现场 · 50 mL 釜'。</summary>
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private bool _everOnline;
    /// <summary>「运行」/「断开前运行」/「断线前运行」。</summary>
    [ObservableProperty] private string _runLabel = "运行";
    [ObservableProperty] private string _runCount = "0";
    [ObservableProperty] private string _runTotal = "/0";
    [ObservableProperty] private string _runTip = "";
    [ObservableProperty] private LeaseState _lease;
    [ObservableProperty] private string _leaseText = "";
    [ObservableProperty] private ConnState _conn;
    [ObservableProperty] private string _connText = "";

    // —— 说明条（断线 / 首次连接；急停） ——
    [ObservableProperty] private bool _hasOffBar;
    [ObservableProperty] private string _offIcon = "wifioff";
    [ObservableProperty] private string _offLead = "";
    [ObservableProperty] private string _offText = "";
    /// <summary>说明条的按钮：「立即重连」/「连接」；首次连接中没有按钮（null）。</summary>
    [ObservableProperty] private string? _offButton;
    [ObservableProperty] private string? _offReason;
    [ObservableProperty] private bool _hasEstopBar;
    [ObservableProperty] private string _estopLead = "";
    [ObservableProperty] private string _estopText = "";

    // —— 从未连上（刚添加）：面板里只有一条说明 ——
    [ObservableProperty] private bool _isNever;
    [ObservableProperty] private string _neverMain = "";
    [ObservableProperty] private string _neverSub = "";

    // —— 版面 ——
    /// <summary>Core 的整页版面（视图还没给尺寸时为 null）。</summary>
    [ObservableProperty] private LayoutPage? _layout;
    /// <summary>上排（反应釜面板）高度；null = 1fr（反应釜面板或整个下排关掉了）。</summary>
    [ObservableProperty] private double? _topRow;
    /// <summary>卡片区纵向滚动（16 路 + 断线 / 急停说明条，1366 / 1440：卡片保持最小高度）。</summary>
    [ObservableProperty] private bool _scroll;
    /// <summary>卡片区内容的高度（滚动时 = 内边距 + 行 × 最小高度 + 行距；否则 = 卡片区高度）。</summary>
    [ObservableProperty] private double _cardsHeight;
    /// <summary>下排两列 + 页签面板（工作区 &lt; 1100）。</summary>
    [ObservableProperty] private bool _tabbed;
    /// <summary>共用资源的内容区窄（≤ 350）：气路两列从标签处开始。</summary>
    [ObservableProperty] private bool _resNarrow;
    /// <summary>设备信息的内容区窄（≤ 339）：「示例型号 · 参数为假设」只留图标。</summary>
    [ObservableProperty] private bool _infoNarrow;
    /// <summary>「报警与事件」的内容区矮（≤ 259）：整块一起滚动（当前报警在最上面完整可见）。</summary>
    [ObservableProperty] private bool _logShort;

    /// <summary>宽工作区的「设备信息」面板的内容（窄工作区时 null：内容在页签面板里，同一份模板只实例化一次）。</summary>
    public DeviceInfoVm? InfoContent => Tabbed ? null : Info;
    /// <summary>宽工作区的「报警与事件」面板的内容。</summary>
    public DeviceLogVm? LogContent => Tabbed ? null : Log;
    /// <summary>窄工作区的页签面板的内容（当前页签：设备信息 / 报警与事件）；宽工作区为 null。</summary>
    public object? TabsContent => !Tabbed ? null : TabLogOn ? Log : Info;

    partial void OnTabbedChanged(bool value)
    {
        OnPropertyChanged(nameof(InfoContent));
        OnPropertyChanged(nameof(LogContent));
        OnPropertyChanged(nameof(TabsContent));
        Log.TabbedLayout = value;
    }

    partial void OnTabLogOnChanged(bool value) => OnPropertyChanged(nameof(TabsContent));

    // —— 窄工作区的页签面板（设备信息 / 报警与事件） ——
    /// <summary>当前页签：'info' / 'log'（PTabs 的 Value，双向：用户点页签时 <see cref="OnTabValueChanged"/> 记下）。</summary>
    [ObservableProperty] private string _tabValue = "info";
    [ObservableProperty] private bool _tabLogOn;
    /// <summary>「报警与事件」页签上的红点（未确认的错误报警）。</summary>
    [ObservableProperty] private bool _tabDot;
    /// <summary>页签上的中性数字（只有未确认的提醒、页签不在前台）；没有为 null。</summary>
    [ObservableProperty] private string? _tabCount;
    [ObservableProperty] private string? _tabTip;

    /// <summary>页签的两项（实例不变，红点 / 数字就地改）。</summary>
    public SegOption InfoTab { get; } = new() { Value = "info", Label = "设备信息" };
    public SegOption LogTab { get; } = new() { Value = "log", Label = "报警与事件" };

    /// <summary>PTabs 的 OptionsSource：「设备信息」「报警与事件」。</summary>
    public IReadOnlyList<SegOption> TabOptions => _tabOptions ??= new[] { InfoTab, LogTab };
    private SegOption[]? _tabOptions;

    /// <summary>视图给的尺寸（设备页根元素 = 工作区，设计 px）；还没有时为 0。</summary>
    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }

    /// <summary>窄工作区页签的自动选择（每个设备页实例一个，样稿 render 时清空）。</summary>
    public DeviceLogTabRule TabRule { get; } = new();

    // —— 面板的放大 / 关闭（视图经 PrPanel 的 IsMaximized / IsClosed 告诉这里；卡片区与下排的取舍跟着变） ——
    private readonly Dictionary<DevicePanelKey, (bool Max, bool Closed)> _panels = new();

    /// <summary>
    /// 卡片的选择变了（单击 / 外框选中别的对象）。卡片与面板的「选中」标记跟着更新。
    /// </summary>
    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelection();

    // ———————————————————— PageVm ————————————————————

    /// <summary>
    /// 「报警与事件」可见（没关、没被别的放大面板盖住；窄工作区时页签在「报警与事件」）：这台设备每条未确认报警的「确认」在页面上（样稿 ownsAlarms）。
    /// </summary>
    public override IReadOnlyDictionary<string, string> OwnedAlarms
    {
        get
        {
            if (D is not { } d || !LogVisible) return NoOwnedAlarms;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in d.AlarmList)
                if (!a.Acked) map[d.K.ToString(CultureInfo.InvariantCulture) + ":" + a.Id] = AlarmTexts.LogOwnerName;
            return map.Count == 0 ? NoOwnedAlarms : map;
        }
    }

    /// <summary>设备页有这台设备的「报警与事件」：检查器的设备正文不再列报警 / 最近事件（样稿 inspDevice 的 o.page === 'device'）。</summary>
    public override bool ShowsAlarmLog => true;

    /// <summary>设备页断线时有说明条（带「立即重连」）：检查器的控制权卡不再放第二个（样稿 leaseBox：打开的页签就是这台设备）。</summary>
    public override bool ShowsReconnect => true;

    /// <summary>藏在别的页签后面时不刷新，切回来时先刷新再显示（R10，见 <see cref="PageVm.RefreshWhenHidden"/>）。</summary>
    public override bool RefreshWhenHidden => false;

    /// <summary>「报警与事件」此刻在屏幕上（面板没关、没有别的面板放大；窄工作区时页签在「报警与事件」）。</summary>
    public bool LogVisible
    {
        get
        {
            var maxed = _panels.FirstOrDefault(p => p.Value.Max).Key;
            if (Tabbed)
            {
                if (Closed(DevicePanelKey.Tabs) || TabRule.Tab != DeviceLogTab.Log) return false;
                return maxed == default || maxed == DevicePanelKey.Tabs;
            }
            if (Closed(DevicePanelKey.Log)) return false;
            return maxed == default || maxed == DevicePanelKey.Log;
        }
    }

    private bool Closed(DevicePanelKey k) => _panels.TryGetValue(k, out var s) && s.Closed;
    private bool Maxed(DevicePanelKey k) => _panels.TryGetValue(k, out var s) && s.Max;

    // ———————————————————— 数据 ————————————————————

    public override void Refresh(IFleetView fleet)
    {
        var d = Shell.Fleet.Device(K);
        if (!ReferenceEquals(d, D))
        {
            D = d;
            _structModel = null;
            OnPropertyChanged(nameof(D));
        }
        if (d == null) return;
        ApplyParams();
        bool on = !d.IsOffline;
        var m = d.Model;
        Crumb = d.Alias + " › 反应釜";
        CrumbTip = d.Alias + " › 反应釜";
        Sub = m.Label + " · " + m.Vessel.Kind;
        Online = on;
        EverOnline = d.EverOnline;
        // 用户自己断开的设备写「断开前运行」，断线写「断线前运行」；从未连上不写运行数（device.js rvPanel）
        RunLabel = on ? "运行" : d.ConnStatus == ConnStatus.Disconnected ? "断开前运行" : "断线前运行";
        RunCount = I(d.Running);
        RunTotal = "/" + I(d.Installed);
        RunTip = RunLabel + " " + I(d.Running) + " / 已安装 " + I(d.Installed) + "（" + m.InstalledText + "，共 " + I(m.ChannelCount) + " 路）";
        Lease = d.Lease;
        LeaseText = d.LeaseText;
        Conn = d.Conn;
        ConnText = d.ConnText;
        RefreshBars(d);
        SyncTiles(d);
        foreach (var t in Tiles)
        {
            if (t is DeviceCardVm c) c.Refresh();
            else if (t is DeviceUntileVm u) u.Refresh();
        }
        UpdateSelection();
        Res.Refresh(d);
        Info.Refresh(d);
        Log.Refresh(d);
        SyncTab(d);
        Relayout();
    }

    /// <summary>演示参数换了（截图 / 演示条）：同样稿每个地址都是新打开的页面 —— 页签规则从头来（打开时有未确认的错误报警就打开「报警与事件」）。</summary>
    private void ApplyParams()
    {
        var p = Shell.Fleet.Params;
        if (p == null || Equals(p, _lastParams)) return;
        bool first = _lastParams == null;
        _lastParams = p;
        if (!first) TabRule.Reset();
        Log.ResetFilter();
    }

    /// <summary>断线 / 首次连接 / 急停的说明条与从未连上的说明（device.js bannersHTML / neverHTML，逐字照样稿）。</summary>
    private void RefreshBars(DeviceVm d)
    {
        var src = d.Source;
        var conn = src.Conn;
        bool on = conn.Online;
        IsNever = !d.EverOnline;
        if (IsNever)
        {
            NeverMain = conn.State == ConnStatus.Connecting ? "正在连接 " + d.Host + ":" + I(d.Port) + "…" : d.ConnText;
            NeverSub = "尚未收到数据 · 连上后这里显示 " + d.Model.InstalledText + "（" + d.Model.Label + "）";
        }
        HasOffBar = !on && !IsNever;
        if (!on)
        {
            bool first = d.IsFirst, disc = conn.State == ConnStatus.Disconnected;
            OffIcon = first ? "link" : disc ? "unplug" : "wifioff";
            OffLead = first ? "尚未收到数据" : d.DataText;
            OffText = first ? "首次连接中 · 连上后这里显示读数"
                : (disc ? "已断开连接 · 下面是断开前的最后读数" : "下面是断线前的最后读数，仅供参考 · 恢复连接后自动刷新")
                  + (src.Lease.State == LeaseStatus.Lost ? " · 控制权宽限 " + I(src.Lease.GraceS) + " s" : "");
            OffButton = first ? null : disc ? "连接" : "立即重连";
            OffReason = OffButton == null ? null : Nz(d.Actions.Why(DeviceAction.Reconnect));
        }
        else
        {
            OffButton = null;
            OffReason = null;
        }
        var estop = src.Estop;
        HasEstopBar = estop.Active && !IsNever;
        if (estop.Active)
        {
            EstopLead = d.EstopText;
            EstopText = (estop.At is { } at ? Core.Format.Fmt.Time(at) + " 触发 · " : "") + "加热 / 进气 / 搅拌已切断 · 复位只能在" + d.Alias + "触屏上操作";
        }
    }

    /// <summary>
    /// 卡片与未安装窄列（Core 版面的块顺序与分组：一排 / 宽卡的未安装段合并成一列，紧凑卡的不跨 8 列的边界）：型号或连上过变了才重排，
    /// 已有的卡片按 RV 号保留（R10）。从未连上的设备没有卡片。
    /// </summary>
    private void SyncTiles(DeviceVm d)
    {
        var model = d.Model;
        bool ever = d.EverOnline;
        if (ReferenceEquals(model, _structModel) && ever == _structEver) return;
        _structModel = model;
        _structEver = ever;
        var want = new List<object>();
        if (ever)
        {
            var page = DeviceLayout.Compute(new DeviceLayoutInput(1200, 800, model));
            foreach (var t in page.Tiles)
            {
                if (t.Installed)
                {
                    var r = d.Reactor(t.From);
                    if (r == null) continue;
                    want.Add(Tiles.OfType<DeviceCardVm>().FirstOrDefault(c => ReferenceEquals(c.R, r)) ?? new DeviceCardVm(this, r));
                }
                else want.Add(Tiles.OfType<DeviceUntileVm>().FirstOrDefault(u => u.From == t.From && u.To == t.To) ?? new DeviceUntileVm(this, t.From, t.To));
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

    // ———————————————————— 版面 ————————————————————

    /// <summary>视图给的尺寸（设备页根元素 = 工作区）。变了才重算版面。</summary>
    public void SetViewport(double width, double height)
    {
        if (Math.Abs(width - ViewWidth) < 0.01 && Math.Abs(height - ViewHeight) < 0.01) return;
        ViewWidth = width;
        ViewHeight = height;
        Relayout();
    }

    /// <summary>面板放大 / 关闭了（视图从 PrPanel 的 IsMaximized / IsClosed 转过来）：卡片区与下排的版面跟着变，检查器重新问页面拥有的报警。</summary>
    public void SetPanel(DevicePanelKey panel, bool max, bool closed)
    {
        if (_panels.TryGetValue(panel, out var s) && s == (max, closed)) return;
        _panels[panel] = (max, closed);
        Relayout();
    }

    /// <summary>视图 › 重置布局 / 换了页面实例：面板的放大 / 关闭都回到默认。</summary>
    public void ResetPanels()
    {
        if (_panels.Count == 0) return;
        _panels.Clear();
        Relayout();
    }

    /// <summary>Core 的整页版面（device.js sizeRoot）：上排高度、卡片区滚动、网格与块、下排各面板的取舍；再交给卡片（就地更新）。</summary>
    internal void Relayout()
    {
        if (D is not { } d || ViewWidth <= 0 || ViewHeight <= 0)
        {
            SyncOwnership();
            return;
        }
        var page = DeviceLayout.Compute(new DeviceLayoutInput(ViewWidth, ViewHeight, d.Model)
        {
            Online = !d.IsOffline,
            EverOnline = d.EverOnline,
            Estop = d.EstopActive,
            RvClosed = Closed(DevicePanelKey.Rv),
            RvMax = Maxed(DevicePanelKey.Rv),
            ResClosed = Closed(DevicePanelKey.Res),
            InfoClosed = Closed(DevicePanelKey.Info),
            LogClosed = Closed(DevicePanelKey.Log),
            TabsClosed = Closed(DevicePanelKey.Tabs),
            LogTab = TabRule.Tab,
        });
        Layout = page;
        TopRow = page.TopRow;
        Scroll = page.Scroll;
        var g = page.Grid;
        int rows = Math.Max(1, g.Rows);
        CardsHeight = page.Scroll ? 2 * g.Pad + rows * page.CardHeight + (rows - 1) * g.Gap : page.CardsHeight;
        // 宽度类变了（跨过 1100：下排「设备信息」「报警与事件」两块 ↔ 页签面板）：这几块面板的放大不带过去（样稿换了一组面板，is-max 跟着旧面板一起没了）——
        // 否则放大的那块跟着宽度类藏起来、别的面板还被放大藏着，整页一片空白（M4 复核）。下面的版面按没有放大算；最后通知视图把 PrPanel 的放大也撤掉
        bool dropMax = Tabbed != page.Tabbed && DropBottomMax();
        Tabbed = page.Tabbed;
        var res = page.Bottom.FirstOrDefault(b => b.Panel == DevicePanel.Resources);
        ResNarrow = res?.Narrow ?? false;
        var info = page.Bottom.FirstOrDefault(b => b.Panel is DevicePanel.Info || b.Panel == DevicePanel.Tabs && TabRule.Tab == DeviceLogTab.Info);
        InfoNarrow = info?.Narrow ?? false;
        var log = page.Bottom.FirstOrDefault(b => b.Panel is DevicePanel.Log || b.Panel == DevicePanel.Tabs && TabRule.Tab == DeviceLogTab.Log);
        // 「报警与事件」放大时内容区 = 整页（PageHost 的原地放大）；否则 = 下排的面板高 − 标题栏
        bool logMax = Maxed(DevicePanelKey.Log) || Maxed(DevicePanelKey.Tabs) && TabRule.Tab == DeviceLogTab.Log;
        double logBody = logMax ? ViewHeight - DeviceLayout.Head : (log?.Height ?? 0) - DeviceLayout.Head;
        LogShort = logMax ? logBody <= 259 : log?.Short ?? false;
        Log.BodyHeight = Math.Max(0, logBody);
        var spec = new DvCardSpec(g.Kind, g.Narrow, g.Tight, Maxed(DevicePanelKey.Rv));
        foreach (var c in Tiles.OfType<DeviceCardVm>()) c.Spec = spec;
        Log.LogShort = LogShort;
        Info.Narrow = InfoNarrow;
        Res.Narrow = ResNarrow;
        SyncOwnership();
        if (dropMax) PanelsUnmaximized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「设备信息」「报警与事件」「页签面板」的放大撤掉（关闭照旧）；返回有没有撤掉什么。</summary>
    private bool DropBottomMax()
    {
        bool any = false;
        foreach (var k in new[] { DevicePanelKey.Info, DevicePanelKey.Log, DevicePanelKey.Tabs })
            if (_panels.TryGetValue(k, out var s) && s.Max)
            {
                _panels[k] = (false, s.Closed);
                any = true;
            }
        return any;
    }

    /// <summary>
    /// 宽度类变了、下排面板的放大撤掉了（<see cref="DropBottomMax"/>）：视图把对应 PrPanel 的 IsMaximized 也撤掉（PageHost 随即还原被藏起来的面板）。
    /// 在 <see cref="Relayout"/> 的最后发出（这一次的版面已经按没有放大算好）。
    /// </summary>
    public event EventHandler? PanelsUnmaximized;

    // ———————————————————— 页签（窄工作区） ————————————————————

    /// <summary>每次刷新：页签规则看未确认的错误报警（新的才切，确认不切）；页签上的红点 / 数字 / 提示。</summary>
    private void SyncTab(DeviceVm d)
    {
        var book = d.Source.AlarmBook;
        var tab = TabRule.Sync(book.UnackedErrIds());
        ApplyTab(tab, d);
    }

    private bool _applyingTab;

    private void ApplyTab(DeviceLogTab tab, DeviceVm d)
    {
        _applyingTab = true;
        try { TabValue = tab == DeviceLogTab.Log ? "log" : "info"; }
        finally { _applyingTab = false; }
        TabLogOn = tab == DeviceLogTab.Log;
        var badge = AlarmTexts.LogTab(d.AlarmList, TabLogOn);
        TabDot = badge.Dot;
        TabCount = badge.Count > 0 ? I(badge.Count) : null;
        TabTip = badge.Tip.Length > 0 ? badge.Tip : null;
        LogTab.Dot = TabDot;
        LogTab.Count = TabCount;
        LogTab.Tip = TabTip;
    }

    /// <summary>用户点了页签（PTabs 双向绑定写回 <see cref="TabValue"/>）：记下「用户自己点过」，之后新的报警也不替用户换页签。</summary>
    partial void OnTabValueChanged(string value)
    {
        if (_applyingTab || D is not { } d) return;
        var tab = value == "log" ? DeviceLogTab.Log : DeviceLogTab.Info;
        if (tab == TabRule.Tab && TabRule.UserPicked) return;
        TabRule.Pick(tab);
        ApplyTab(tab, d);
        TabPicked?.Invoke(this, EventArgs.Empty);
        Relayout();
    }

    /// <summary>用户换了页签（视图把内容滚回顶部，同样稿 onClick 的 body.scrollTop = 0）。</summary>
    public event EventHandler? TabPicked;

    // ———————————————————— 选中 ————————————————————

    private void UpdateSelection()
    {
        var sel = Shell.Selection;
        foreach (var c in Tiles.OfType<DeviceCardVm>()) c.IsSelected = sel.K == K && sel.N == c.N;
    }

    /// <summary>「报警与事件」出现 / 消失（或未确认的报警变了）：检查器重新问一次页面拥有的报警（样稿 paint 末尾的 syncInspector）。</summary>
    private void SyncOwnership()
    {
        var sig = string.Join(",", OwnedAlarms.Keys.OrderBy(x => x, StringComparer.Ordinal));
        if (sig == _ownSig) return;
        _ownSig = sig;
        if (ReferenceEquals(Shell.Page, this)) Shell.Inspector.Refresh();
    }

    public override void Dispose() => Shell.Selection.Changed -= OnSelectionChanged;

    internal static string I(int n) => n.ToString(CultureInfo.InvariantCulture);

    internal static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>设备页的面板（视图 → <see cref="DevicePageVm.SetPanel"/>）。</summary>
public enum DevicePanelKey { None, Rv, Res, Info, Log, Tabs }

/// <summary>卡片的种类与网格给的取舍（Core 的 DeviceGrid：窄紧凑卡、8 路窄卡）+ 反应釜面板放大（紧凑卡画完整釜体）。DvCard 拿它和自己的尺寸算卡片内部的版面。</summary>
public sealed record DvCardSpec(CardKind Kind, bool Narrow, bool Tight, bool Maximized)
{
    public static readonly DvCardSpec Default = new(CardKind.Row, false, false, false);
}
