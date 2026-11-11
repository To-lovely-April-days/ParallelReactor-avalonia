using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell.Dialogs;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 新外壳的根视图模型（PORT-PLAN §5 M2「Shell/」；样稿 shell.js 的 PR.shell）：菜单、页签、图标栏、状态栏、全局停止格、toast、对话框宿主、
/// 选择（<see cref="Selection"/>，检查器显示谁）、资源管理器（<see cref="Explorer"/>）、页面（每个页签一个页面视图模型）、外框布局。
/// 数据只从 <see cref="Fleet"/>（Fleet/FleetVm，实现 IFleetView）读；数据变化（合并过的 Changed）或布局变化时各部分就地刷新，不重建集合。
/// 导航：<see cref="Navigate"/>（页签不在栏里时追加；设备 / 反应釜页同时选中该对象）。选择：<see cref="Select"/>。
/// 演示：<see cref="ApplyDemoParams"/> 套用样稿的地址参数（devs、sel、lease、offline、alarm、estop、explorer、tl、stress、freeze、dialog）；
/// 交互走查启动用 <see cref="ApplyStartParams"/>，截图用 <see cref="ResetView"/> + <see cref="ApplyDemoParams"/>（都同样稿新开的页面）。
/// </summary>
public sealed partial class ShellVm : ObservableObject, IDisposable
{
    /// <summary>产品名（品牌格提示、窗口标题；样稿 .pr-brand 的 title）。</summary>
    public const string AppTitle = "HT 平行反应仪 · 控制软件";

    /// <summary>帮助 › 关于。</summary>
    public static string AboutText
    {
        get
        {
            var v = typeof(ShellVm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = v.IndexOf('+');
            return "HT 平行反应仪控制软件" + (v.Length > 0 ? " · " + (plus > 0 ? v[..plus] : v) : "");
        }
    }

    private readonly Dictionary<string, PageVm> _pages = new(StringComparer.Ordinal);

    public ShellVm(FleetVm fleet, ShellLayout? layout = null)
    {
        Fleet = fleet;
        Layout = layout ?? new ShellLayout();
        Toasts = new ToastService();
        Dialogs = new DialogHostVm((t, k) => Toasts.Show(t, k));
        Selection = new SelectionVm(fleet);
        Tabs = new TabsVm(() => Fleet, Navigate);
        Sidebar = new SidebarVm(this);
        Status = new StatusBarVm(this);
        StopCell = new StopCellVm(this);
        Explorer = new ExplorerVm(this);
        Inspector = new InspectorVm(this);
        Timeline = new TimelineVm(this);

        Fleet.Changed += OnFleetChanged;
        Fleet.Toast += OnFleetToast;
        Layout.Changed += OnLayoutChanged;
        Selection.Changed += OnSelectionChanged;
        Tabs.Tabs.CollectionChanged += OnTabsChanged;

        // 样稿的对话框（M3）：演示与真机同一套（Shell/Dialogs）
        DialogSet.Register(this);

        Tabs.ResetToDefaults();
        _route = Route.Overview;
        Selection.Prune();
        Navigate(Route.Overview);
    }

    public FleetVm Fleet { get; }
    public ShellLayout Layout { get; }
    public ToastService Toasts { get; }
    public DialogHostVm Dialogs { get; }
    public SelectionVm Selection { get; }
    public TabsVm Tabs { get; }
    public SidebarVm Sidebar { get; }
    public StatusBarVm Status { get; }
    public StopCellVm StopCell { get; }
    public ExplorerVm Explorer { get; }
    public InspectorVm Inspector { get; }
    public TimelineVm Timeline { get; }

    /// <summary>打开着的页面（每个页签一个；PageHost 给每个一份视图）。</summary>
    public ObservableCollection<PageVm> Pages { get; } = new();

    [ObservableProperty] private Route _route;
    [ObservableProperty] private PageVm? _page;

    /// <summary>窗口标题：演示时带「演示数据」（R17）。</summary>
    public string WindowTitle => AppTitle + (Fleet.IsDemo ? " · 演示数据" : "");

    /// <summary>菜单栏右端「本机：PC-测试工位1」。</summary>
    public string ClientText => "本机：" + Fleet.ClientName;

    // ---------------- 选择（检查器的对象） ----------------

    /// <summary>选中的设备（没有设备时 null）。</summary>
    public DeviceVm? SelectedDevice => Selection.Device;

    /// <summary>选中的反应釜 RV 号（选中的是设备时 null）。</summary>
    public int? SelectedRv => SelectedDevice == null ? null : Selection.N;

    /// <summary>选中设备或反应釜（样稿 PR.shell.select(k, n?)）：检查器切到该对象；资源管理器展开收起的设备并高亮。</summary>
    public void Select(int k, int? n = null) => Selection.Select(k, n);

    private int _lastSelK;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        Explorer.Reveal(Selection.K, Selection.N);
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedRv));
        Explorer.Refresh();
        Inspector.Refresh();
        Timeline.Refresh();
        Status.Refresh();
        // 时间轴跟随（样稿 select → tlFollow(prevK !== sel.k)）
        bool changedDevice = _lastSelK != Selection.K;
        _lastSelK = Selection.K;
        Timeline.Follow(changedDevice);
    }

    // ---------------- 导航 ----------------

    /// <summary>
    /// 打开（或切到）页签（样稿 PR.route.go）：设备 / 反应釜不存在时回到总览；设备页 / 反应釜页同时选中该对象；
    /// 浮层资源管理器在导航后收起。
    /// </summary>
    public void Navigate(Route route)
    {
        if (!TabsVm.Routable(Fleet, route)) route = Route.Overview;
        Tabs.Activate(route);
        Route = route;
        Page = PageFor(route);
        if (route.Page == PageKind.Device) Select(route.K);
        else if (route.Page == PageKind.Reactor) Select(route.K, route.N);
        else Selection.Prune();
        Layout.CloseFloat();
        PrunePages();
        RefreshChrome();
        Explorer.RequestReveal();
        Timeline.Follow(true);
    }

    private PageVm PageFor(Route r)
    {
        if (_pages.TryGetValue(r.Id, out var p))
        {
            // 藏着时没有刷新的页面（总览 / 设备页，R10）：显示之前先跟上数据
            if (!p.RefreshWhenHidden && !ReferenceEquals(p, Page)) p.Refresh(Fleet);
            return p;
        }
        p = r.Page switch
        {
            PageKind.Device => new DevicePageVm(this, r),
            PageKind.Reactor => new ReactorPageVm(this, r),
            // 程序编辑 / 下发 / 导出与性能测试（D5）：M2 起以旧样式挂在新外壳的页签里，M7 换新组件；其余第二阶段视图是占位页
            PageKind.Placeholder when r.Key == "program" => new LegacyProgramPageVm(this),
            PageKind.Placeholder when r.Key == "perf" => new PerfPageVm(this),
            PageKind.Placeholder => new PlaceholderPageVm(r),
            PageKind.Gallery => new GalleryPageVm(),
            _ => new OverviewPageVm(this),
        };
        p.Refresh(Fleet);
        _pages[r.Id] = p;
        Pages.Add(p);
        return p;
    }

    /// <summary>已经打开的某种页面（没有为 null）。</summary>
    public T? OpenPage<T>() where T : PageVm => Pages.OfType<T>().FirstOrDefault();

    /// <summary>页签关掉了：它的页面（和 PageHost 里的视图）一起释放。</summary>
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Replace) PrunePages();
    }

    /// <summary>去掉页签已经不在的页面（当前页面除外：关掉当前页签时，切到别的页签之后再去掉它）。性能测试在跑时页面留着（引擎还在用它）。</summary>
    private void PrunePages()
    {
        var open = Tabs.Tabs.Select(t => t.Id).ToHashSet();
        foreach (var id in _pages.Keys.Where(id => !open.Contains(id)).ToList())
        {
            var p = _pages[id];
            if (ReferenceEquals(p, Page) || p is PerfPageVm { IsBusy: true }) continue;
            _pages.Remove(id);
            Pages.Remove(p);
            p.Dispose();
        }
    }

    // ---------------- 演示参数 ----------------

    /// <summary>
    /// 套用样稿的地址参数（README「常用查询参数」）：数据（devs / lease / offline / alarm / estop / stress / freeze）交给 FleetVm（只对演示数据有效），
    /// 外框（explorer / tl）交给 ShellLayout；sel 选中检查器对象（设备页 / 反应釜页选中自己），给了 <paramref name="route"/> 时再导航过去
    /// （tl、sel 在导航之前套用，explorer 在导航之后），dialog 打开对话框（对话框在 M3：没注册的给「未定义」提示）。
    /// </summary>
    public void ApplyDemoParams(DemoParams p, Route? route = null)
    {
        var devsBefore = Fleet.Params?.Devs + "|" + Fleet.Params?.Stress;
        Fleet.ApplyDemoParams(p);
        // 换了设备组合：页签回到默认（样稿 pruneTabs 的 _tabsFor）
        if (Fleet.Params?.Devs + "|" + Fleet.Params?.Stress != devsBefore) Tabs.ResetToDefaults();
        Tabs.Refresh();
        var dest = route ?? (TabsVm.Routable(Fleet, Route) ? null : Route.Overview);
        var kind = dest == null ? Route.Page : TabsVm.Routable(Fleet, dest) ? dest.Page : PageKind.Overview;
        // 样稿 shell.render 的顺序：先读 tl，再 syncSel（设备页 / 反应釜页选中自己，其它页面按 sel），最后跟随（tlFollow）——
        // 时间轴收起和 sel 都在导航之前套用，导航的时间轴跟随才按新页面的对象展开组（复核：以前先导航，跟随了上一个选中对象，展开了它的 16 路组）
        if (p.TimelineCollapsed is bool tc) Layout.ApplyParams(null, tc ? "0" : "1");
        if (p.Sel is { } s && kind is not (PageKind.Device or PageKind.Reactor)) Select(s.K, s.N);
        if (dest != null) Navigate(dest);
        // 外框参数在导航之后（再）套用：导航会收起浮层资源管理器（Navigate → CloseFloat），explorer=1 在窄屏上要的正是浮层打开
        Layout.ApplyParams(p.Explorer is bool x ? (x ? "1" : "0") : null, p.TimelineCollapsed is bool t ? (t ? "0" : "1") : null);
        Selection.Prune();
        RefreshChrome();
        if (p.DialogName is { Length: > 0 } dn) OpenDialog(dn, p.DialogArg);
    }

    /// <summary>
    /// 演示的交互走查启动（ShotRunner.StartDemo：PR_CONTROL_ROUTE「路径?参数」，没给路径时总览），同样稿新开的页面：构造时导航到总览已经跟随了第一台
    /// （16 路时展开它的组）—— 先清掉时间轴的展开 / 跟随状态，再套用参数并导航，只由这次导航跟随（同截图的 ResetView → ApplyDemoParams）。
    /// 复核：以前直接 ApplyDemoParams，sel / 路由是别的设备时第一台的 16 路组还展开着；没给路径时不导航，清了之后就没有跟随，所以总是导航。
    /// </summary>
    public void ApplyStartParams(DemoParams p, Route? route)
    {
        Timeline.ResetOpen(fresh: true);
        ApplyDemoParams(p, route ?? Route.Overview);
    }

    /// <summary>
    /// 回到刚打开时的样子（截图：样稿的每张参照图都是新开的页面）：默认外框布局与页签、关掉对话框与浮层、选中第一台设备、
    /// 资源管理器与时间轴的展开状态回到默认、树滚回顶部；页面里关掉 / 放大的面板还原（<see cref="LayoutReset"/>），提示清空
    /// （M5：反应釜页的截图准备会关掉「程序」并弹提示，下一张截图不能带着它们）。之后由 <see cref="ApplyDemoParams"/> 导航到截图的页面。
    /// </summary>
    public void ResetView()
    {
        Dialogs.Close();
        Toasts.Clear();
        LayoutReset?.Invoke(this, EventArgs.Empty);
        Layout.Reset();
        Timeline.ResetSpan();
        Tabs.ResetToDefaults();
        Explorer.ResetView();
        var first = Fleet.Devices.FirstOrDefault();
        if (first != null) Select(first.K);
        Navigate(Route.Overview);
        // 时间轴的展开状态最后才清：上面的选中 / 导航都会跟随（Follow），选中时路由还是上一张截图的页面，会把那一台（如 16 路的 3号机）展开
        // （复核：截图的结果取决于前一张）。清完之后只有接下来的导航跟随一次，同样稿新开的页面
        Timeline.ResetOpen(fresh: true);
    }

    // ---------------- 动作 ----------------

    public void Toast(string text, ToastKind kind = ToastKind.Ok) => Toasts.Show(text, kind);

    private void OnFleetToast(string text, ToastKind kind) => Toasts.Show(text, kind);

    public void OpenDialog(string name, string? arg = null) => Dialogs.Open(name, arg);

    /// <summary>
    /// 断开一台设备（样稿 shell.requestDisconnect）：本机持有或正在申请它的控制权时先打开断开确认（断开会释放控制权 / 取消申请），否则直接断开。
    /// </summary>
    public void RequestDisconnect(IFleetDevice d)
    {
        if (d.Lease is Controls.Pr.LeaseState.Remote or Controls.Pr.LeaseState.Lost or Controls.Pr.LeaseState.Requested) OpenDialog("disconnect", d.K.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else DeviceAct(d.K, "disconnect");
    }

    public void ToggleExplorer() => Layout.ToggleExplorer();

    public void ToggleTimeline() => Layout.ToggleTimeline();

    public void ToggleFocus()
    {
        var on = Layout.ToggleFocus();
        Toast(on ? "已收起资源管理器与时间轴" : "已展开资源管理器与时间轴", ToastKind.Info);
    }

    /// <summary>
    /// 视图 › 重置布局（样稿 resetLayout）：外框（资源管理器、时间轴、专注）+ 时间轴跨度 + 资源管理器与时间轴的展开状态回到默认
    /// （样稿 xOpen = {}、tlOpen = {}：不跟随、不滚动）+ 页面里放大 / 关闭的面板（<see cref="LayoutReset"/>，PageHost 处理）。
    /// </summary>
    public void ResetLayout()
    {
        Layout.Reset();
        Timeline.ResetSpan();
        Explorer.ResetOpen();
        Timeline.ResetOpen();
        LayoutReset?.Invoke(this, EventArgs.Empty);
        RefreshChrome();
        Toast("布局已重置", ToastKind.Info);
    }

    /// <summary>重置布局时页面要恢复的东西（放大 / 关闭的面板）。</summary>
    public event EventHandler? LayoutReset;

    public void CloseOtherTabs() => Tabs.CloseOthers();

    /// <summary>文件 › 退出（由窗口处理：关窗 → 正常的关闭流程）。</summary>
    public event EventHandler? ExitRequested;

    public void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 退出前要先问的设备（真机，R7，M3 复核）：本机的控制权申请（或已取消、还在等结果的申请）还在它的触屏上 —— 直接退出的话触屏之后若批准，
    /// 这份控制权没人心跳，宽限期满 HMI 按 onRemoteLost 处理。
    /// </summary>
    public IReadOnlyList<DeviceVm> PendingLeaseRequests =>
        Fleet.Devices.Where(d => d.Source is LiveDeviceSource { LeaseRequestPending: true }).ToList();

    /// <summary>
    /// 关窗 / 退出之前（App）：有申请还在触屏上时打开退出确认（<see cref="ExitDialogVm"/>）并返回 false —— 之后用户选「仍然退出」或等到了结果时触发
    /// <see cref="ExitConfirmed"/>；没有时返回 true（照常退出）。
    /// </summary>
    public bool TryExit()
    {
        if (PendingLeaseRequests.Count == 0) return true;
        if (Dialogs.Current is not ExitDialogVm) OpenDialog("exit");
        return false;
    }

    /// <summary>退出确认里选了「仍然退出」，或者等到了每条申请的结果：App 接着走正常的关闭流程。</summary>
    public event EventHandler? ExitConfirmed;

    internal void ConfirmExit() => ExitConfirmed?.Invoke(this, EventArgs.Empty);

    /// <summary>设备动作（菜单「释放控制权」「立即重连」…）：交给 FleetVm → DeviceActions（守卫、执行、提示都在那里）。</summary>
    public void DeviceAct(int k, string action)
    {
        var msg = Fleet.Act(k, action);
        if (!string.IsNullOrEmpty(msg)) Toast(msg, ToastKind.Warn);
    }

    /// <summary>
    /// Esc（样稿 keydown Escape 的外框部分）：先关对话框；再收起浮层资源管理器。打开的菜单 / 弹出框由 Avalonia 自己关；
    /// 放大的面板由 PageHost 先处理。返回是否处理了。
    /// </summary>
    public bool Escape()
    {
        if (Dialogs.IsOpen) { Dialogs.Close(); return true; }
        if (Layout.ExplorerFloat) { Layout.CloseFloat(); return true; }
        return false;
    }

    // ---------------- 刷新 ----------------

    private void OnFleetChanged(object? sender, EventArgs e)
    {
        Tabs.Refresh();
        if (!TabsVm.Routable(Fleet, Route)) { Navigate(Route.Overview); return; }
        Selection.Prune();
        // 看得见的页面每次都刷新；藏在别的页签后面的总览 / 设备页等切回来时再刷新（PageVm.RefreshWhenHidden，R10）
        foreach (var p in Pages) if (p.RefreshWhenHidden || ReferenceEquals(p, Page)) p.Refresh(Fleet);
        OnPropertyChanged(nameof(ClientText));
        RefreshChrome();
        // 对话框跟着数据刷新（样稿 refresh() 里的 renderDialog：计数、说明、倒计时；对象设备没了时关闭）
        Dialogs.RefreshCurrent();
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => RefreshChrome();

    /// <summary>外框各部分按当前数据 / 路由 / 布局刷新文字和标记。</summary>
    public void RefreshChrome()
    {
        Tabs.Refresh();
        Sidebar.Refresh();
        Status.Refresh();
        StopCell.Refresh();
        Explorer.Refresh();
        Inspector.Refresh();
        Timeline.Refresh();
    }

    // ---------------- 退出 ----------------

    /// <summary>
    /// 退出前收尾（在 UI 线程上开始）：先中止运行中的性能测试（停止类命令收尾 + 出报告，≤ 3 s），再释放全部数据源
    /// （实时：释放控制权 ≤ 3 s、摘掉事件、断开）。整体不超过 <paramref name="timeout"/>。
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        var perf = OpenPage<PerfPageVm>();
        var work = Task.Run(async () =>
        {
            if (perf != null) await perf.Perf.AbortAndWaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Fleet.DisposeAsync().ConfigureAwait(false);
        });
        await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Fleet.Changed -= OnFleetChanged;
        Fleet.Toast -= OnFleetToast;
        Layout.Changed -= OnLayoutChanged;
        Selection.Changed -= OnSelectionChanged;
        Tabs.Tabs.CollectionChanged -= OnTabsChanged;
        foreach (var p in Pages) p.Dispose();
        Toasts.Dispose();
    }
}
