using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using LayoutPage = ParallelReactor.Control.Core.Layout.OverviewPage;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 总览（样稿 js/pages/overview.js + css/overview.css，PORT-PLAN §2.2「总览」、§5 M4）：每台设备一个分区（<see cref="Sections"/>，身份 = 设备位置 K），
/// 分区头一行安静的状态、按型号排的卡片网格（宽卡 / 一排卡 / 紧凑卡 + 合并的「RV5–RV8 未安装」虚线块）、断线 / 急停 / 首次连接的说明条；
/// 内容超出一屏且有未确认的错误报警时，顶部固定一条报警条（<see cref="Strip"/>：「定位」+ 这条报警屏幕上唯一的「确认」—— 页面声明 <see cref="OwnedAlarms"/>，
/// 检查器里只写「确认在顶部报警条」）。没有设备时是空状态（「还没有设备」+ 唯一的主要操作「添加设备」）。
/// <para>
/// 版面：视图把总览根元素的尺寸（= 工作区，父元素给的尺寸，R4）交给 <see cref="SetViewport"/>，这里用 Core 的 <see cref="OverviewLayout"/>
/// （样稿 cardKind / bodyHTML / paint 的数字规则）算出每个分区的网格、卡片按自身内容宽的取舍、报警条、窄工作区去掉的分区头内容，
/// 再交给各分区 / 卡片（就地更新，R10）。下方剩余 ≥ 200 px（没有报警条、没有放大的分区）时最后一个分区下面是「温度趋势」面板（<see cref="Trend"/>，M5 的曲线控件）。
/// </para>
/// 数据：<see cref="Refresh"/>（外框在每次数据变化后调）按 FleetVm 的设备就地增删分区、刷新文字；选中变化时更新分区头 / 卡片的选中标记。
/// </summary>
public sealed partial class OverviewPageVm : PageVm
{
    private DemoParams? _lastParams;

    public OverviewPageVm(ShellVm shell) : base(Route.Overview)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Strip = new OverviewStripVm(this);
        AddCommand = new RelayCommand(() => Shell.OpenDialog("add"));
        Shell.Selection.Changed += OnSelectionChanged;
    }

    public ShellVm Shell { get; }

    /// <summary>每台设备一个分区，按设备位置排列（实例在设备的生命周期内不变）。</summary>
    public ObservableCollection<OverviewSectionVm> Sections { get; } = new();

    /// <summary>顶部报警条（内容超出一屏且有未确认的错误报警时显示）。</summary>
    public OverviewStripVm Strip { get; }

    /// <summary>空状态的「添加设备」。</summary>
    public ICommand AddCommand { get; }

    /// <summary>还没有设备（空状态）。</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>工作区 ≤ 900：分区头不写「记录中」（状态栏一直显示会话，overview.css:261）。</summary>
    [ObservableProperty] private bool _hideSession;

    /// <summary>工作区 ≤ 820：分区头不写型号（overview.css:264）。</summary>
    [ObservableProperty] private bool _hideModel;

    /// <summary>滚动区的高度（总览高度减去报警条）：放大的分区铺满它。</summary>
    [ObservableProperty] private double _scrollHeight;

    /// <summary>放大着的分区（设备位置；null = 没有）：其它分区不显示，滚动区停在顶部、不滚动（overview.css:13）。</summary>
    [ObservableProperty] private int? _maxK;

    /// <summary>视图给的总览尺寸（宽 × 高，设计 px）；还没有时为 0。</summary>
    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }

    /// <summary>最近一次算出的整页版面（Core）；视图还没给尺寸时为 null。</summary>
    public LayoutPage? Layout { get; private set; }

    /// <summary>
    /// 「温度趋势」面板（样稿 overview.js：没有报警条、各分区下面剩余 ≥ 200 px 时补在最后一个分区下面，铺满剩余的高度；放大一个分区时不显示）。
    /// 显示着时每次刷新 / 换了选中的设备都跟着重算（数据没变时不重建）。
    /// </summary>
    public OverviewTrendVm Trend { get; } = new();

    /// <summary>温度趋势显示着时滚动区内容的最小高度（= 滚动区高度：趋势面板铺满分区下面剩余的地方）；不显示时为 0。</summary>
    [ObservableProperty] private double _trendFill;

    /// <summary>请视图滚动：到某个分区的顶部（「定位」）、到底（演示参数 scroll=end）或回到顶部（换了演示参数，同样稿每张参照图都是新打开的页面）。</summary>
    public event EventHandler<OverviewScrollRequest>? ScrollRequested;

    /// <summary>
    /// 视图还没处理的滚动请求（最新的一个）：请求可能在视图建好之前（页面刚建、<see cref="Refresh"/> 先跑）或总览藏着的时候发出 ——
    /// 视图在排好版、显示出来之后处理它，处理完 <see cref="TakeScroll"/>（M4 复核：从别的页面回到总览时 scroll=end 丢了）。
    /// </summary>
    public OverviewScrollRequest? PendingScroll { get; private set; }

    /// <summary>视图处理了滚动请求：清掉 <see cref="PendingScroll"/>，返回它。</summary>
    public OverviewScrollRequest? TakeScroll()
    {
        var r = PendingScroll;
        PendingScroll = null;
        return r;
    }

    private void RequestScroll(OverviewScrollRequest r)
    {
        PendingScroll = r;
        ScrollRequested?.Invoke(this, r);
    }

    /// <summary>
    /// 顶部报警条上那条报警的「确认」在页面上（样稿 ownsAlarms：{ 'k:id': '顶部报警条' }）：检查器只列出这条报警（中性卡片「确认在顶部报警条」），
    /// 不再放第二个确认按钮。没有报警条时为空表。
    /// </summary>
    public override IReadOnlyDictionary<string, string> OwnedAlarms =>
        Strip.IsVisible && Strip.OwnedKey is { } key ? new Dictionary<string, string> { [key] = AlarmStrip.OwnerName } : NoOwnedAlarms;

    /// <summary>藏在别的页签后面时不刷新，切回来时先刷新再显示（R10，见 <see cref="PageVm.RefreshWhenHidden"/>）。</summary>
    public override bool RefreshWhenHidden => false;

    // ———————————————————— 数据 ————————————————————

    public override void Refresh(IFleetView fleet)
    {
        var devs = Shell.Fleet.Devices;
        // 稳定身份：按设备（位置 K + 实例）增删、移动，已有的就地刷新
        for (int i = Sections.Count - 1; i >= 0; i--)
        {
            var s = Sections[i];
            var d = Shell.Fleet.Device(s.K);
            if (d == null || !ReferenceEquals(d, s.D)) Sections.RemoveAt(i);
        }
        for (int i = 0; i < devs.Count; i++)
        {
            var d = devs[i];
            int at = -1;
            for (int j = 0; j < Sections.Count; j++) if (ReferenceEquals(Sections[j].D, d)) { at = j; break; }
            OverviewSectionVm s;
            if (at < 0) { s = new OverviewSectionVm(this, d); Sections.Insert(Math.Min(i, Sections.Count), s); }
            else
            {
                s = Sections[at];
                if (at != i) Sections.Move(at, i);
            }
            s.Refresh();
        }
        IsEmpty = devs.Count == 0;
        if (MaxK is int mk && Sections.All(s => s.K != mk)) MaxK = null;
        UpdateSelection();
        Relayout();
        ApplyParams();
    }

    /// <summary>演示参数换了（截图 / 演示条）：scroll=end 滚到底，否则回到顶部（同样稿：每个地址都是新打开的页面）。</summary>
    private void ApplyParams()
    {
        var p = Shell.Fleet.Params;
        if (p == null || Equals(p, _lastParams)) return;
        _lastParams = p;
        RequestScroll(p.ScrollToEnd ? OverviewScrollRequest.End : OverviewScrollRequest.Top);
    }

    /// <summary>视图给的尺寸（总览根元素 = 工作区）。变了才重算版面。</summary>
    public void SetViewport(double width, double height)
    {
        if (Math.Abs(width - ViewWidth) < 0.01 && Math.Abs(height - ViewHeight) < 0.01) return;
        ViewWidth = width;
        ViewHeight = height;
        Relayout();
    }

    /// <summary>
    /// 按尺寸与数据重算整页版面（样稿 paint）：每个分区（型号、连上过、说明条、最后一个没有底边、有没有带进度条的卡片）→ Core 的分区版面；
    /// 报警条 = 有未确认的错误报警且各分区总高超出一屏（与报警条显示与否无关，不会来回振荡）。报警条出现 / 消失时检查器跟上（ownsAlarms）。
    /// </summary>
    internal void Relayout()
    {
        var strip = AlarmStrip.Build(Sections.Select(s => new AlarmStripSource(s.K, s.D.Alias, s.D.AlarmList)));
        Strip.Apply(strip);
        if (ViewWidth <= 0 || ViewHeight <= 0)
        {
            Layout = null;
            Strip.IsVisible = false;
            SyncOwnership();
            return;
        }
        // 分区版面只在它的输入（型号、宽度、连上过、说明条、是不是最后一个、有没有进度条）变了时重算：每秒的刷新不重排卡片（R10）
        var secs = new List<OverviewSection>(Sections.Count);
        for (int i = 0; i < Sections.Count; i++) secs.Add(Sections[i].LayoutFor(ViewWidth, last: i == Sections.Count - 1));
        var page = OverviewLayout.Page(ViewWidth, ViewHeight, secs, strip.Any, drawsTrend: true);
        Layout = page;
        HideSession = page.HideSession;
        HideModel = page.HideModel;
        ScrollHeight = page.ScrollHeight;
        for (int i = 0; i < Sections.Count; i++) Sections[i].ApplyLayout(page.Sections[i]);
        Strip.IsVisible = page.AlarmStrip;
        SyncTrend();
        SyncOwnership();
    }

    /// <summary>温度趋势：版面说要画、没有放大的分区时显示，并按当前的设备与选中重算曲线（样稿 paint 末尾的 syncChart）。</summary>
    private void SyncTrend()
    {
        bool show = Layout?.Trend == true && MaxK is null && !IsEmpty;
        Trend.IsShown = show;
        TrendFill = show ? Layout!.ScrollHeight : 0;
        if (show) Trend.Refresh(Shell.Fleet.Devices, Shell.Selection.K, Shell.Fleet.Now);
    }

    /// <summary>报警条出现 / 消失（或换了一条）：检查器重新问一次页面拥有的报警（样稿 paint 末尾的 syncInspector）。</summary>
    private void SyncOwnership()
    {
        bool now = Strip.IsVisible;
        var key = now ? Strip.OwnedKey : null;
        var sig = now + "|" + key;
        if (sig == _ownSig) return;
        _ownSig = sig;
        if (ReferenceEquals(Shell.Page, this)) Shell.Inspector.Refresh();
    }

    private string _ownSig = "False|";

    // ———————————————————— 选中 ————————————————————

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelection();
        // 温度趋势画的是选中的设备
        if (Trend.IsShown) Trend.Refresh(Shell.Fleet.Devices, Shell.Selection.K, Shell.Fleet.Now);
    }

    private void UpdateSelection()
    {
        var sel = Shell.Selection;
        foreach (var s in Sections) s.UpdateSelection(sel.K, sel.N);
    }

    // ———————————————————— 操作 ————————————————————

    /// <summary>
    /// 报警条的「定位」（样稿 data-ov-locate）：滚到那台设备的分区顶部，再选中那个反应釜（设备级报警选中设备）—— 检查器跟过去，
    /// 那条报警在检查器里是中性卡片「确认在顶部报警条」。
    /// </summary>
    internal void Locate(int k, int? n)
    {
        if (MaxK is int mk && mk != k) MaxK = null;
        RequestScroll(OverviewScrollRequest.To(k));
        Shell.Select(k, n);
    }

    /// <summary>分区放大 / 还原（PrPanel 的 □，样稿 .pr-panel.is-max 以滚动区为定位框）：视图保存 / 恢复滚动位置。</summary>
    public void SetMax(int k, bool on)
    {
        if (on) MaxK = k;
        else if (MaxK == k) MaxK = null;
    }

    partial void OnMaxKChanged(int? value)
    {
        foreach (var s in Sections) s.UpdateMax(value);
        SyncTrend();
    }

    public override void Dispose() => Shell.Selection.Changed -= OnSelectionChanged;
}

/// <summary>滚动请求：到某个分区（<see cref="K"/>）、到底、或回到顶部。</summary>
public sealed record OverviewScrollRequest(int K, bool ToEnd, bool ToTop)
{
    public static readonly OverviewScrollRequest End = new(0, true, false);
    public static readonly OverviewScrollRequest Top = new(0, false, true);
    public static OverviewScrollRequest To(int k) => new(k, false, false);
}

/// <summary>
/// 总览顶部的报警条（样稿 alarmBarHTML；Core 的 <see cref="AlarmStrip"/> 给文字）：「⚠ 3号机 · RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11 … 10:09:42 另有 n 条 [定位] [确认]」。
/// 只显示最新的一条未确认错误报警（急停那条不列：各处已有红色「急停中」）。「确认」不需要控制权，断线时带原因（守卫）。
/// </summary>
public sealed partial class OverviewStripVm : ObservableObject
{
    private readonly OverviewPageVm _page;
    private AlarmStrip _strip = AlarmStrip.Empty;

    public OverviewStripVm(OverviewPageVm page)
    {
        _page = page;
        LocateCommand = new RelayCommand(() => { if (_strip.Locate is { } l) _page.Locate(l.K, l.Ch); });
        AckCommand = new RelayCommand(() =>
        {
            if (_strip.First is not { } f || _page.Shell.Fleet.Device(f.K) is not { } d) return;
            _ = d.Actions.RunAsync(Core.Guard.DeviceAction.AlarmAck, new Core.Guard.ActionPayload(Id: f.Alarm.Id));
        });
    }

    /// <summary>显示（版面决定：有报警且内容超出一屏）。</summary>
    [ObservableProperty] private bool _isVisible;
    /// <summary>有未确认的错误报警（不管版面）。</summary>
    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private string _who = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _more = "";
    /// <summary>「确认」不能用的原因（断线等）；能用时 null。</summary>
    [ObservableProperty] private string? _ackReason;

    public ICommand LocateCommand { get; }
    public ICommand AckCommand { get; }

    /// <summary>条上那条报警的「k:id」（检查器据此不放第二个确认按钮）。</summary>
    public string? OwnedKey => _strip.OwnedKey;

    /// <summary>条上的那条（测试用）。</summary>
    public AlarmStripItem? First => _strip.First;

    internal void Apply(AlarmStrip strip)
    {
        _strip = strip;
        HasItems = strip.Any;
        Who = strip.Who;
        Text = strip.Text;
        Title = strip.Title;
        Time = strip.Time;
        More = strip.More;
        AckReason = strip.First is { } f && _page.Shell.Fleet.Device(f.K) is { } d
            ? Nz(d.Actions.Why(Core.Guard.DeviceAction.AlarmAck, new Core.Guard.ActionPayload(Id: f.Alarm.Id)))
            : null;
        OnPropertyChanged(nameof(OwnedKey));
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}
