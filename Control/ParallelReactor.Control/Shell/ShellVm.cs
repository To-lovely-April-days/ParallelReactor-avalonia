using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
/// 外壳的根视图模型（融合版 F2，样稿 main-fusion.html）：深蓝条、菜单栏（页面 + 运行胶囊 + 报警摘要 + 本机）、工具条、停靠区（设备面板 · 看板 · 检查器）、状态栏，
/// 以及对话框宿主、toast、选择（检查器显示谁）。
/// <list type="bullet">
/// <item>路由 = <see cref="Nav"/>（页面 + 设备 + 通道，取代 pc-v3 的页签）。<see cref="Go"/> 换页面，<see cref="SelectDevice"/> / <see cref="SelectChannel"/> 选设备 / 通道。</item>
/// <item>页面：每种一个实例（<see cref="Pages"/>，按需创建），<see cref="Page"/> 是当前的。页面的工具条按钮见 <see cref="PageVm.Tools"/>。</item>
/// <item>布局 = <see cref="Layout"/>（只看窗口尺寸）：设备面板 / 检查器停靠、收起或浮出。</item>
/// <item>检查器（<see cref="Inspector"/>，pc-v3 的检查器，F3 重做样式）显示 <see cref="Selection"/>：跟着路由选的设备 / 通道。</item>
/// </list>
/// 数据只从 <see cref="Fleet"/> 读；数据、路由或布局变了时各部分就地刷新（<see cref="RefreshChrome"/>）。
/// 演示：<see cref="ApplyDemoParams"/> 套用样稿的地址参数（page、dev、ch、po / pc、alm，以及 pc-v3 的数据参数）；截图前 <see cref="ResetView"/>。
/// </summary>
public sealed partial class ShellVm : ObservableObject, IDisposable
{
    /// <summary>产品名（窗口标题）。</summary>
    public const string AppTitle = "ALZK 平行反应仪控制软件";

    /// <summary>深蓝条上的产品名（样稿 .tb-title，后面接「— n 台设备」）。</summary>
    public const string ProductTitle = "ALZK Parallel Reactor Control · 平行反应仪控制软件";

    /// <summary>关于。</summary>
    public static string AboutText
    {
        get
        {
            var v = typeof(ShellVm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = v.IndexOf('+');
            return "ALZK 平行反应仪控制软件" + (v.Length > 0 ? " · " + (plus > 0 ? v[..plus] : v) : "");
        }
    }

    private readonly Dictionary<AppPage, PageVm> _pages = new();

    public ShellVm(FleetVm fleet, DockLayout? layout = null)
    {
        Fleet = fleet;
        Layout = layout ?? new DockLayout();
        Nav = new NavVm(fleet);
        Toasts = new ToastService();
        Dialogs = new DialogHostVm((t, k) => Toasts.Show(t, k));
        Selection = new SelectionVm(fleet);
        Menubar = new MenubarVm(this);
        Capsule = new CapsuleVm(this);
        Alarms = new AlarmSummaryVm(this);
        Ribbon = new RibbonVm(this);
        Devices = new DevicePaneVm(this);
        Status = new StatusBarVm(this);
        Inspector = new InspectorVm(this);
        AlarmPop = new AlarmPopVm(this);
        Ctl = new ChannelCtlVm(this);
        Dialogs.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DialogHostVm.IsOpen)) AlarmPop.Refresh(); };

        Fleet.Changed += OnFleetChanged;
        Fleet.Toast += OnFleetToast;
        Layout.Changed += OnLayoutChanged;
        Nav.Changed += OnNavChanged;
        Selection.Changed += OnSelectionChanged;

        DialogSet.Register(this);

        // 深蓝条右端：设置 / 关于 / 帮助都到「设置」页（F8 在那里做「关于」「帮助」两节），工具（通讯日志、诊断包）F9
        SettingsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Go(AppPage.Settings));
        AboutCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { Go(AppPage.Settings); Toast(AboutText, ToastKind.Info); });
        HelpCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { Go(AppPage.Settings); Toast("使用说明在「设置 › 帮助」（F8）", ToastKind.Info); });
        ToolsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Toast("工具：通讯日志 · 导出诊断包（F9）", ToastKind.Info));
        // 深蓝条上的工位名：改本机客户端名（真机注册了 client 对话框；演示数据改不了）
        ClientCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
        {
            if (Dialogs.IsRegistered("client") && !Fleet.IsDemo) OpenDialog("client");
            else Toast("演示数据不能改本机客户端名", ToastKind.Info);
        });

        Nav.Reset();
    }

    public FleetVm Fleet { get; }
    public DockLayout Layout { get; }
    public NavVm Nav { get; }
    public ToastService Toasts { get; }
    public DialogHostVm Dialogs { get; }
    public SelectionVm Selection { get; }
    public MenubarVm Menubar { get; }
    public CapsuleVm Capsule { get; }
    public AlarmSummaryVm Alarms { get; }
    public RibbonVm Ribbon { get; }
    public DevicePaneVm Devices { get; }
    public StatusBarVm Status { get; }
    public InspectorVm Inspector { get; }
    /// <summary>报警弹窗（融合版 F3，样稿 alarmPop）。</summary>
    public AlarmPopVm AlarmPop { get; }
    /// <summary>主页检查器（融合版 F3：选中的一路，样稿 ctlHTML）。</summary>
    public ChannelCtlVm Ctl { get; }

    public System.Windows.Input.ICommand SettingsCommand { get; }
    public System.Windows.Input.ICommand AboutCommand { get; }
    public System.Windows.Input.ICommand HelpCommand { get; }
    public System.Windows.Input.ICommand ToolsCommand { get; }
    public System.Windows.Input.ICommand ClientCommand { get; }

    /// <summary>已经建过的页面（PageHost 给每个一份视图）。</summary>
    public ObservableCollection<PageVm> Pages { get; } = new();

    [ObservableProperty] private PageVm? _page;
    /// <summary>深蓝条的标题（「ALZK Parallel Reactor Control · 平行反应仪控制软件 — 3 台设备」）。</summary>
    [ObservableProperty] private string _stripTitle = ProductTitle;
    /// <summary>
    /// 检查器那一列在不在（样稿：检查器是主页看板的一部分 —— 只有「主页 + 选了设备」时有；总览和其它页面没有，看板铺到右边）。
    /// </summary>
    [ObservableProperty] private bool _showInspector = true;
    /// <summary>窗口全屏（工具条「全屏」/ F11；MainWindow 照它改 WindowState）。</summary>
    [ObservableProperty] private bool _isFullScreen;

    /// <summary>窗口标题：演示时带「演示数据」。</summary>
    public string WindowTitle => AppTitle + (Fleet.IsDemo ? " · 演示数据" : "");

    /// <summary>本机客户端名（深蓝条的工位名、菜单栏右端「本机」）。</summary>
    public string ClientName => Fleet.ClientName;

    // ---------------- 选择（检查器的对象） ----------------

    /// <summary>检查器的设备（没有设备时 null）。</summary>
    public DeviceVm? SelectedDevice => Selection.Device;

    /// <summary>检查器的反应釜 RV 号（检查器显示的是设备时 null）。</summary>
    public int? SelectedRv => SelectedDevice == null ? null : Selection.N;

    /// <summary>选中设备或它的一路（检查器里的链接等）：路由跟着走（主页 / 当前页面选中这台设备、这一路）。</summary>
    public void Select(int k, int? n = null)
    {
        if (n is int) _channelPicked.Add(k); else _channelPicked.Remove(k);
        if (n is int nn) SelectChannel(k, nn);
        else SelectDevice(k);
        Selection.Select(k, n);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedRv));
        Inspector.Refresh();
    }

    // ---------------- 路由 ----------------

    /// <summary>换页面（菜单栏、深蓝条「设置」、Ctrl+1…6）：关掉报警列表、收回浮出的面板。</summary>
    public void Go(AppPage page)
    {
        Alarms.IsOpen = false;
        Layout.CloseFloats();
        if (!Nav.Go(page)) RefreshChrome();
    }

    /// <summary>选设备（0 = 总览 / 全部设备）。</summary>
    public void SelectDevice(int k) => Nav.SelectDevice(k);

    /// <summary>选通道（同时选中设备）。</summary>
    public void SelectChannel(int k, int n) => Nav.SelectChannel(k, n);

    private void OnNavChanged(object? sender, EventArgs e)
    {
        Page = PageFor(Nav.Page);
        // 检查器跟着路由：选了设备 = 这台设备（显式选过通道时是那一路，F3 起点运行表的行）
        if (Nav.DeviceK > 0) Selection.Select(Nav.DeviceK, _channelPicked.Contains(Nav.DeviceK) ? Nav.Channel : null);
        else Selection.Prune();
        Page.Refresh(Fleet);
        RefreshChrome();
    }

    /// <summary>显式选过通道的设备（检查器显示那一路；只是选了设备时检查器显示设备）。</summary>
    private readonly HashSet<int> _channelPicked = new();

    /// <summary>选通道并打开检查器（F3 运行表的行、设备面板的通道）：样稿 SEL + CTL_OPEN + PCOL.delete('pInsp')。</summary>
    public void OpenChannel(int k, int n)
    {
        _channelPicked.Add(k);
        if (!Nav.SelectChannel(k, n)) Selection.Select(k, n);
        Layout.Open(DockPaneId.Inspector);
    }

    private PageVm PageFor(AppPage p)
    {
        if (_pages.TryGetValue(p, out var vm)) return vm;
        vm = p switch
        {
            AppPage.Home => new HomePageVm(this),
            // 程序（F6 前）沿用旧样式的程序页（编辑 / 下发 / 导出）；性能测试（F8 前）沿用旧样式的性能测试页
            AppPage.Program => new LegacyProgramPageVm(this),
            AppPage.Perf => new PerfPageVm(this),
            AppPage.Gallery => new GalleryPageVm(),
            _ => new PlaceholderPageVm(p, this),
        };
        vm.ToolsChanged += (_, _) => { if (ReferenceEquals(vm, Page)) Ribbon.SetTools(vm.Tools); };
        _pages[p] = vm;
        Pages.Add(vm);
        return vm;
    }

    /// <summary>已经建过的某种页面（没有为 null）。</summary>
    public T? OpenPage<T>() where T : PageVm => Pages.OfType<T>().FirstOrDefault();

    // ---------------- 演示参数 ----------------

    /// <summary>
    /// 套用样稿的地址参数：数据（devs / lease / offline / alarm / estop / stress / freeze）交给 FleetVm（只对演示数据有效）；
    /// 路由 page=main|prog|curve|alarm|test|rec|set|gallery、dev=k（0 / 不给 = 总览）、ch=n；面板 po / pc（展开 / 收起：dev、insp）；
    /// alm=1 打开报警列表；dialog=name[:arg] 打开对话框。没给 page 时不换页面；没给 page / dev 时 pc-v3 的 sel=k[/n] 选中设备（和通道）。
    /// </summary>
    public void ApplyDemoParams(DemoParams p)
    {
        string? X(string key) => p.Extra.FirstOrDefault(kv => kv.Key == key).Value;
        static int Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        // 断线横条（样稿 net=lost / back，融合版 F3）：lost = 这台断线（数据停在断线时刻）；back = 当作刚断开 2 min 26 s 又连上、补拉了 146 条
        int netK = Math.Max(1, Int(X("dev")));
        if (X("net") == "lost" && p.Offline is null) p = p with { Offline = new[] { netK.ToString(CultureInfo.InvariantCulture) } };
        Fleet.ApplyDemoParams(p);
        if (X("net") == "back" && Fleet.Device(netK) is { } nb) nb.ForceNetBack(Fleet.Now.AddSeconds(-146), Fleet.Now, 146);
        AlarmPop.Off = X("apop") == "0";
        var page = AppPages.Parse(X("page"));
        if (page != null || X("dev") != null)
        {
            int k = Int(X("dev"));
            int? n = X("ch") is { } c ? Int(c) : null;
            if (n is int nn && k > 0) _channelPicked.Add(k);
            Nav.Set(page ?? Nav.Page, k, n);
        }
        // pc-v3 的 sel=k / k/n（检查器的对象）：选中这台设备（和这一路）
        if (page == null && X("dev") == null && p.Sel is { } sel && Fleet.Device(sel.K) != null) Select(sel.K, sel.N);
        if (X("po") != null || X("pc") != null) Layout.ApplyParams(X("po"), X("pc"));
        if (X("alm") == "1") Alarms.IsOpen = true;
        if (X("ovm") is { Length: > 0 } ovm && OpenPage<HomePageVm>() is { } hp) hp.Curve.ModeKey = ovm;
        Nav.Prune();
        Page?.Refresh(Fleet);
        RefreshChrome();
        if (p.DialogName is { Length: > 0 } dn) OpenDialog(dn, p.DialogArg);
    }

    /// <summary>交互走查启动（ShotRunner.StartDemo：PR_CONTROL_ROUTE 的参数）。</summary>
    public void ApplyStartParams(DemoParams p) => ApplyDemoParams(p);

    /// <summary>
    /// 回到刚打开时的样子（截图：样稿的每张参照图都是新开的页面）：关掉对话框、报警列表、提示；默认布局；主页 · 1号机 · 每台 RV1；检查器显示 1号机。
    /// </summary>
    public void ResetView()
    {
        Dialogs.Close();
        Toasts.Clear();
        Alarms.IsOpen = false;
        Alarms.Filter = "all";
        _channelPicked.Clear();
        Layout.Reset();
        OpenPage<HomePageVm>()?.ResetBoard();
        AlarmPop.Reset(false);
        Nav.Reset();
    }

    // ---------------- 动作 ----------------

    public void Toast(string text, ToastKind kind = ToastKind.Ok) => Toasts.Show(text, kind);

    private void OnFleetToast(string text, ToastKind kind) => Toasts.Show(text, kind);

    public void OpenDialog(string name, string? arg = null)
    {
        Alarms.IsOpen = false;
        Dialogs.Open(name, arg);
    }

    /// <summary>断开一台设备：本机持有或正在申请它的控制权时先打开断开确认，否则直接断开。</summary>
    public void RequestDisconnect(IFleetDevice d)
    {
        if (d.Lease is Controls.Pr.LeaseState.Remote or Controls.Pr.LeaseState.Lost or Controls.Pr.LeaseState.Requested) OpenDialog("disconnect", d.K.ToString(CultureInfo.InvariantCulture));
        else DeviceAct(d.K, "disconnect");
    }

    /// <summary>重置布局（工具条「重置布局」，样稿 reset）：面板回到默认、关掉报警列表、页面里的面板还原（<see cref="LayoutReset"/>）。</summary>
    public void ResetLayout()
    {
        Alarms.IsOpen = false;
        Layout.Reset();
        LayoutReset?.Invoke(this, EventArgs.Empty);
        RefreshChrome();
        Toast("布局已恢复默认", ToastKind.Info);
    }

    /// <summary>重置布局时页面要恢复的东西（放大 / 关闭的面板，F3 起）。</summary>
    public event EventHandler? LayoutReset;

    /// <summary>全屏开关（工具条「全屏」/ F11）。</summary>
    public void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    /// <summary>退出（关窗）。</summary>
    public event EventHandler? ExitRequested;

    public void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>退出前要先问的设备（真机）：本机的控制权申请还在它的触屏上。</summary>
    public IReadOnlyList<DeviceVm> PendingLeaseRequests =>
        Fleet.Devices.Where(d => d.Source is LiveDeviceSource { LeaseRequestPending: true }).ToList();

    /// <summary>关窗 / 退出之前：有申请还在触屏上时打开退出确认并返回 false；没有时返回 true。</summary>
    public bool TryExit()
    {
        if (PendingLeaseRequests.Count == 0) return true;
        if (Dialogs.Current is not ExitDialogVm) OpenDialog("exit");
        return false;
    }

    /// <summary>退出确认里选了「仍然退出」，或者等到了每条申请的结果。</summary>
    public event EventHandler? ExitConfirmed;

    internal void ConfirmExit() => ExitConfirmed?.Invoke(this, EventArgs.Empty);

    /// <summary>设备动作（「释放控制权」「立即重连」…）：交给 FleetVm → DeviceActions。</summary>
    public void DeviceAct(int k, string action)
    {
        var msg = Fleet.Act(k, action);
        if (!string.IsNullOrEmpty(msg)) Toast(msg, ToastKind.Warn);
    }

    /// <summary>
    /// Esc（样稿 keydown Escape 的外框部分，一次只做一步）：对话框 → 报警列表 → 浮出的面板。返回是否处理了。
    /// </summary>
    public bool Escape()
    {
        if (Dialogs.IsOpen) { Dialogs.Close(); return true; }
        if (AlarmPop.IsOpen) { AlarmPop.LaterCommand.Execute(null); return true; }
        if (Alarms.IsOpen) { Alarms.IsOpen = false; return true; }
        if (Layout.CloseFloats()) return true;
        return false;
    }

    // ---------------- 刷新 ----------------

    private void OnFleetChanged(object? sender, EventArgs e)
    {
        if (Nav.Prune()) return;   // 路由变了：OnNavChanged 已经刷新了一遍
        Selection.Prune();
        foreach (var p in Pages) if (p.RefreshWhenHidden || ReferenceEquals(p, Page)) p.Refresh(Fleet);
        OnPropertyChanged(nameof(ClientName));
        RefreshChrome();
        Dialogs.RefreshCurrent();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        Page?.Refresh(Fleet);
        RefreshChrome();
    }

    /// <summary>外框各部分按当前数据 / 路由 / 布局刷新文字和标记。</summary>
    public void RefreshChrome()
    {
        ShowInspector = Nav.Page == AppPage.Home && Nav.DeviceK > 0;
        StripTitle = ProductTitle + " — " + Fleet.Devices.Count.ToString(CultureInfo.InvariantCulture) + " 台设备";
        Menubar.Refresh();
        Capsule.Refresh();
        Alarms.Refresh();
        Ribbon.Refresh();
        if (Page != null) Ribbon.SetTools(Page.Tools);
        Devices.Refresh();
        Status.Refresh();
        Inspector.Refresh();
        Ctl.Refresh();
        AlarmPop.Refresh();
    }

    // ---------------- 退出 ----------------

    /// <summary>退出前收尾：先中止运行中的性能测试，再释放全部数据源。整体不超过 <paramref name="timeout"/>。</summary>
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
        Nav.Changed -= OnNavChanged;
        Selection.Changed -= OnSelectionChanged;
        foreach (var p in Pages) p.Dispose();
        Toasts.Dispose();
    }
}
