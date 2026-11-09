using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Client;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Fleet;

/// <summary>
/// 同时连接的全部设备（PORT-PLAN §5 M2「FleetVm」）：每台一个 <see cref="DeviceVm"/>（身份 = 位置 K），数据来自各自的 <see cref="IDeviceSource"/>。
/// 实现外框的 <see cref="IFleetView"/>（菜单、页签、状态栏、全局停止格、资源管理器都读它）。
/// <list type="bullet">
/// <item>演示（<see cref="CreateDemo"/>）：Core 的 DemoFleet（样稿 sim.js 的移植），地址参数 devs / lease / offline / alarm / estop / stress / freeze 经
///   <see cref="ApplyDemoParams"/> 套用；换 devs / stress 时演示数据整个重建，DeviceVm 按位置保留、换数据源。</item>
/// <item>实时（<see cref="CreateLive"/>）：多台（M3），设备列表来自 devices.json（<see cref="DeviceRegistry"/>：增删改、排序、启动时自动连接、退出时释放控制权）；
///   对话框（添加设备、编辑设置、设备管理…）经下面「设备管理」一节的方法改它，演示数据同一套方法改 DemoFleet。每台设备自己做重连监督
///   （LiveDeviceSource.StartAsync：首次连接失败倒计时重试、WS 断线的倒计时、立即重连），各自的事件合并成一次刷新后排到 UI 线程，这里再合并一次。</item>
/// </list>
/// 刷新：数据源的变化、操作结果、每秒（<see cref="OnTick"/>，UiTicker）都只是<b>请求</b>刷新（<see cref="RequestRefresh"/>），排一次到 UI 线程，
/// 合并后就地刷新全部 DeviceVm，再触发一次 <see cref="Changed"/>（外框跟着刷新）。全部成员只在 UI 线程上用。
/// </summary>
public sealed partial class FleetVm : ObservableObject, IFleetView, IAsyncDisposable
{
    private readonly Action<Action> _post;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<ClientOptions, IReactorConnection>? _connFactory;
    /// <summary>实时设备的倒计时 / 重试等待用的时钟（null = 系统时钟；测试 / 截图场景给假时钟）。</summary>
    private IClock? _clock;
    private readonly List<IDeviceSource> _sources = new();
    private DemoFleet? _demo;
    private bool _refreshPending, _disposed;
    private bool _stopAllStopsStirring = ControlOptions.StopAllStopsStirring;

    private FleetVm(bool demo, string clientName, Action<Action>? post, SynchronizationContext? uiContext, Func<ClientOptions, IReactorConnection>? connFactory)
    {
        IsDemo = demo;
        ClientName = clientName;
        _post = post ?? (a => a());
        _uiContext = uiContext;
        _connFactory = connFactory;
    }

    /// <summary>
    /// 演示数据（样稿的地址参数，见 Core.Demo.DemoParams）。<paramref name="post"/> = 把一次刷新排到 UI 线程（App：Dispatcher.UIThread.Post；
    /// 测试 / 不给：立即执行）。
    /// </summary>
    public static FleetVm CreateDemo(DemoParams? p = null, Action<Action>? post = null)
    {
        var f = new FleetVm(true, DemoFleet.Client, post, null, null);
        f._demo = new DemoFleet(p ?? DemoParams.Default) { StopAllStopsStirring = f._stopAllStopsStirring };
        f.Params = f._demo.Params;
        f._demo.Changed += f.OnDemoChanged;
        f._demo.Toast += f.OnDemoToast;
        f.SyncDemoDevices();
        f.RefreshNow();
        return f;
    }

    /// <summary>
    /// 实时：设备列表来自 devices.json（<paramref name="config"/>，经 <see cref="DeviceRegistry"/>）；开始时谁都不连，<see cref="StartAsync"/> 并行连接
    /// 勾了「启动时自动连接」且记住了令牌的设备（D7）。<paramref name="connFactory"/> / <paramref name="httpFactory"/> = 测试用的假连接 / 假 HTTP。
    /// 不写文件的配置（截图、演示、测试）用 ControlConfig.InMemory 或 CanWrite = false 的配置。
    /// <paramref name="clock"/> = 每台设备的重试等待与倒计时用的时钟（与连接工厂给 Client 的时钟一致；null = 系统时钟）。
    /// </summary>
    public static FleetVm CreateLive(ControlConfig config, Action<Action>? post = null, SynchronizationContext? uiContext = null,
        Func<ClientOptions, IReactorConnection>? connFactory = null, Func<System.Net.Http.HttpMessageHandler>? httpFactory = null, IClock? clock = null)
    {
        var f = new FleetVm(false, config.ClientName, post, uiContext, connFactory) { Config = config };
        f._httpFactory = httpFactory;
        f._clock = clock;
        f.Registry = new DeviceRegistry(config, f.NewLiveSource);
        f.Registry.Changed += f.OnRegistryChanged;
        config.SaveFailed += f.OnSaveFailed;
        f.SyncLiveDevices();
        return f;
    }

    // ———————————————————— IFleetView ————————————————————

    /// <summary>本机客户端名（菜单栏右端「本机：…」；实时设备申请控制权时报给触屏）。实时可在「设备 › 本机客户端名…」里改（<see cref="SetClientName"/>）。</summary>
    public string ClientName { get; private set; }
    public bool IsDemo { get; }

    /// <summary>
    /// 「全部停止」连搅拌一起停（HMI POST /stop-all 的 stopStir）。默认取 Core 的 <see cref="ControlOptions.StopAllStopsStirring"/>（全软件只有那一处设置，
    /// 用户已确认：连搅拌一起停）；界面上没有入口，只有测试换值。演示数据和之后新连的实时设备都用这个值；全部停止对话框的副标题、说明和提示也按它写（Core 的 StopAllText）。
    /// </summary>
    public bool StopAllStopsStirring
    {
        get => _stopAllStopsStirring;
        set
        {
            _stopAllStopsStirring = value;
            if (_demo != null) _demo.StopAllStopsStirring = value;
        }
    }
    public bool HasSampleModels => DeviceList.Any(d => d.Model.Hypothetical);
    public DateTime Now => _demo?.Now ?? DateTime.Now;

    /// <summary>全部设备，按位置排序（实例在设备的生命周期内不变）。</summary>
    public ObservableCollection<DeviceVm> DeviceList { get; } = new();
    public IReadOnlyList<DeviceVm> Devices => DeviceList;
    IReadOnlyList<IFleetDevice> IFleetView.Devices => DeviceList;

    public DeviceVm? Device(int k)
    {
        foreach (var d in DeviceList) if (d.K == k) return d;
        return null;
    }

    IFleetDevice? IFleetView.Device(int k) => Device(k);

    /// <summary>外框菜单的设备动作（样稿代码 leaseRelease / reconnect / stopAll …）：交给 DeviceActions（守卫、执行、提示都在那里），返回 null。</summary>
    public string? Act(int k, string action)
    {
        var d = Device(k);
        if (d == null) return "设备不存在";
        if (!CoreAction(action, out var a)) return "未知操作 " + action;
        _ = d.Actions.RunAsync(a);
        return null;
    }

    private static bool CoreAction(string code, out DeviceAction a) => Core.Guard.DeviceActions.TryParse(code, out a);

    public event EventHandler? Changed;

    /// <summary>要给用户的提示（操作结果、原因、控制权迁移、演示的超时……），ShellVm 交给 ToastService。</summary>
    public event Action<string, ToastKind>? Toast;

    internal void RaiseToast(string text, ToastKind kind)
    {
        if (!string.IsNullOrWhiteSpace(text)) Toast?.Invoke(text, kind);
    }

    // ———————————————————— 刷新 ————————————————————

    /// <summary>请求一次刷新（合并：已经排上的不再排）。</summary>
    public void RequestRefresh()
    {
        if (_refreshPending || _disposed) return;
        _refreshPending = true;
        _post(() => { _refreshPending = false; RefreshNow(); });
    }

    /// <summary>立即刷新全部设备并通知外框（UI 线程）。</summary>
    public void RefreshNow()
    {
        if (_disposed) return;
        var now = Now;
        foreach (var d in DeviceList) d.Refresh(now);
        OnPropertyChanged(nameof(HasSampleModels));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>每秒（UiTicker）：演示数据推进一秒（冻结时不推进）、实时源的倒计时；时钟要走，所以总是刷新一次。</summary>
    public void OnTick(bool advance)
    {
        if (_disposed) return;
        if (advance && _demo is { } demo) demo.Tick();
        foreach (var s in _sources.OfType<LiveDeviceSource>()) s.Tick();
        RequestRefresh();
    }

    private void OnSourceChanged(SourceChange c)
    {
        InvalidateFacts();
        RequestRefresh();
    }

    /// <summary>数据变了（还没刷新）：守卫的事实立即作废，刷新之前问到的原因也是新的。</summary>
    private void InvalidateFacts()
    {
        foreach (var d in DeviceList) d.InvalidateFacts();
    }

    private void Attach(IDeviceSource s)
    {
        _sources.Add(s);
        s.Changed += OnSourceChanged;
        if (s is LiveDeviceSource live) live.Notice += RaiseToast;
    }

    private void DetachSource(IDeviceSource s)
    {
        _sources.Remove(s);
        s.Changed -= OnSourceChanged;
        if (s is LiveDeviceSource live) live.Notice -= RaiseToast;
    }

    // ———————————————————— 演示 ————————————————————

    /// <summary>演示数据（实时为 null）。</summary>
    public DemoFleet? DemoData => _demo;

    /// <summary>最后一次套用的演示参数。</summary>
    [ObservableProperty] private DemoParams? _params;

    /// <summary>套用地址参数（样稿 applyParams：换 devs / stress 重建，其余按差异）。只对演示有效。</summary>
    public void ApplyDemoParams(DemoParams p)
    {
        if (_demo is null) return;
        _demo.Apply(p);
        Params = _demo.Params;
        SyncDemoDevices();
        RefreshNow();
    }

    /// <summary>
    /// 演示数据整个重来（同样稿新开页面：场景从头生成，再套用这些参数）—— 截图一张一张拍时用，上一张的状态（急停、断线、控制权……）
    /// 不会留到下一张。设备的视图模型按位置保留。
    /// </summary>
    public void ResetDemo(DemoParams p)
    {
        if (_demo is null) return;
        _demo.Apply(DemoParams.Default with { Devs = p.Devs, Stress = p.Stress, Freeze = p.Freeze });
        _demo.Reset();
        _demo.Apply(p);
        Params = _demo.Params;
        SyncDemoDevices();
        RefreshNow();
    }

    private void OnDemoChanged(object? sender, DemoChangedEventArgs e)
    {
        if (e.Kind == DemoChangeKind.Devices) SyncDemoDevices();
        InvalidateFacts();
        RequestRefresh();
    }

    private void OnDemoToast(object? sender, DemoToastEventArgs e) => RaiseToast(e.Text, e.Kind switch
    {
        DemoToastKind.Info => ToastKind.Info,
        DemoToastKind.Warn => ToastKind.Warn,
        DemoToastKind.Err => ToastKind.Err,
        _ => ToastKind.Ok,
    });

    /// <summary>DeviceVm 跟着演示设备走：按位置保留实例，演示设备换了（重建）就换数据源；多了的加、少了的去掉。</summary>
    private void SyncDemoDevices()
    {
        if (_demo is null) return;
        var list = _demo.Devices;
        foreach (var gone in DeviceList.Where(d => list.All(x => x.K != d.K)).ToList())
        {
            DetachSource(gone.Source);
            _ = gone.Source.DisposeAsync();
            DeviceList.Remove(gone);
        }
        int i = 0;
        foreach (var dev in list)
        {
            var vm = Device(dev.K);
            if (vm == null)
            {
                var src = new DemoDeviceSource(_demo, dev);
                Attach(src);
                vm = new DeviceVm(this, dev.K, src);
                DeviceList.Insert(Math.Min(i, DeviceList.Count), vm);
            }
            else if (vm.Source is not DemoDeviceSource ds || !ReferenceEquals(ds.Device, dev))
            {
                DetachSource(vm.Source);
                _ = vm.Source.DisposeAsync();
                var src = new DemoDeviceSource(_demo, dev);
                Attach(src);
                vm.SetSource(src);
            }
            int at = DeviceList.IndexOf(vm);
            if (at != i) DeviceList.Move(at, i);
            i++;
        }
    }

    // ———————————————————— 实时（M3：多台，设备列表来自 DeviceRegistry） ————————————————————

    /// <summary>本机配置（实时；演示为 null）。</summary>
    public ControlConfig? Config { get; private set; }

    /// <summary>实时设备的登记表（演示为 null）：增删改、排序、启动时自动连接、退出时释放控制权。</summary>
    public DeviceRegistry? Registry { get; private set; }

    /// <summary>「测试连接」用的 HTTP 处理器工厂（测试给脚本化的假服务端；null = 系统默认）。</summary>
    private Func<System.Net.Http.HttpMessageHandler>? _httpFactory;

    /// <summary>
    /// 程序页 / 性能测试页（旧样式）要操作的那台实时设备：第一台在线的，没有时第一台（按设备管理里的顺序）。M7 给这两页加设备选择。
    /// </summary>
    public LiveDeviceSource? Live =>
        _sources.OfType<LiveDeviceSource>().Where(s => s.Conn.Online).Select(s => (s, Index: IndexOfSource(s))).OrderBy(x => x.Index).Select(x => x.s).FirstOrDefault()
        ?? _sources.OfType<LiveDeviceSource>().Select(s => (s, Index: IndexOfSource(s))).OrderBy(x => x.Index).Select(x => x.s).FirstOrDefault();

    private int IndexOfSource(IDeviceSource s)
    {
        for (int i = 0; i < DeviceList.Count; i++) if (ReferenceEquals(DeviceList[i].Source, s)) return i;
        return int.MaxValue;
    }

    /// <summary>实时设备的连接（性能测试、程序页、导出用）；没有连上为 null。</summary>
    public IReactorConnection? LiveConnection => Live?.Connection;

    /// <summary>实时设备的视图模型（同 <see cref="Live"/>）。</summary>
    public DeviceVm? LiveDevice => Live is { } l ? DeviceList.FirstOrDefault(d => ReferenceEquals(d.Source, l)) : null;

    /// <summary>
    /// 新建一台实时设备的数据源（DeviceRegistry 用）：客户端名、UI 线程、连接工厂、「全部停止停不停搅拌」（ControlOptions 的值经 FleetVm 传下去）、
    /// devices.json 里记下的序列号与型号（还没连上时显示用）。
    /// </summary>
    private LiveDeviceSource NewLiveSource(DeviceEntry e) => new(e.ToDeviceConfig(), ClientName, _post, _connFactory, _uiContext, clock: _clock)
    {
        StopAllStopsStirring = StopAllStopsStirring,
        LastSerial = e.LastSerial,
        InitialModelKey = e.LastModel,
    };

    /// <summary>启动：并行连接全部「启动时自动连接」且记住了令牌的设备（D7）。不抛。演示数据什么都不做。</summary>
    public Task StartAsync() => Registry?.StartAsync() ?? Task.CompletedTask;

    /// <summary>DeviceVm 跟着登记表走：按 K 保留实例，顺序同登记表；多了的加、少了的去掉（就地，不重建集合）。</summary>
    private void SyncLiveDevices()
    {
        if (Registry is not { } reg) return;
        var list = reg.Devices;
        foreach (var gone in DeviceList.Where(d => list.All(x => x.K != d.K)).ToList())
        {
            DetachSource(gone.Source);
            DeviceList.Remove(gone);
        }
        int i = 0;
        foreach (var rd in list)
        {
            var vm = Device(rd.K);
            if (vm == null)
            {
                Attach(rd.Source);
                vm = new DeviceVm(this, rd.K, rd.Source);
                DeviceList.Insert(Math.Min(i, DeviceList.Count), vm);
            }
            int at = DeviceList.IndexOf(vm);
            if (at != i) DeviceList.Move(at, i);
            i++;
        }
        InvalidateFacts();
        RefreshNow();
    }

    private void OnRegistryChanged() => SyncLiveDevices();

    /// <summary>实时设备的本机操作（改名、移除；不发给 HMI）。</summary>
    internal async Task<ActionOutcome> LocalActionAsync(DeviceVm d, DeviceAction action, ActionPayload p)
    {
        if (d.Source is not LiveDeviceSource || Registry is not { } reg || reg.ByK(d.K) is not { } rd) return ActionOutcome.Failed("设备不存在");
        if (action == DeviceAction.Rename)
        {
            var old = d.Alias;
            var err = reg.Rename(rd.Id, p.Alias ?? "");
            if (err.Length > 0) return ActionOutcome.Failed(err);
            return ActionOutcome.Done(rd.Entry.Alias + " · 已重命名（原名 " + old + "）");
        }
        var alias = d.Alias;
        await reg.RemoveAsync(rd.Id).ConfigureAwait(true);
        return ActionOutcome.Done("已移除 " + alias);
    }

    // ———————————————————— 设备管理（对话框：添加 / 编辑 / 设备管理 / 重命名；演示与实时同一套） ————————————————————

    /// <summary>第一个没被占用的「k号机」（样稿 freeAlias：添加设备的默认别名）。</summary>
    public string FreeAlias() => _demo?.FreeAlias() ?? Registry?.FreeAlias() ?? ActionGuard.FreeAlias(DeviceList.Select(d => d.Alias));

    /// <summary>别名检查（样稿 aliasError）：'' = 可用；<paramref name="exceptK"/> = 正在改的那台。</summary>
    public string AliasError(string? alias, int? exceptK)
    {
        if (_demo is { } demo) return demo.AliasError(alias, exceptK is int k ? demo.Device(k) : null);
        if (Registry is { } reg) return reg.AliasError(alias, exceptK is int k2 ? reg.ByK(k2)?.Id : null);
        return ActionGuard.AliasError(alias, DeviceList.Where(d => d.K != exceptK).Select(d => new PeerDevice(d.Alias, d.Host)));
    }

    /// <summary>一台设备当前的设置（编辑设置对话框的初值）；没有这台为 null。演示数据的令牌是演示值；实时设备没记住、也没输过令牌时为空串。</summary>
    public DeviceSettings? SettingsOf(int k)
    {
        if (_demo?.Device(k) is { } dd)
            return new DeviceSettings(k, dd.Alias, dd.Host, dd.Port, dd.Token, RememberToken: true, dd.AutoConnect, dd.ModelKey, dd.Model.Label + " · " + dd.Model.ChannelCount + " 路", dd.Sn);
        if (Registry?.ByK(k) is { } rd)
        {
            var e = rd.Entry;
            var m = rd.Source.Model;
            return new DeviceSettings(k, e.Alias, e.Host, e.Port, e.Token ?? "", e.RememberToken, e.AutoConnect && e.RememberToken, m.Key,
                m.Label + " · " + m.ChannelCount + " 路", rd.Source.Sn);
        }
        return null;
    }

    /// <summary>
    /// 「测试连接」：实时 = GET /health（不带令牌）再 GET /device（带令牌），见 Services/ConnectionTester；演示 = 样稿的写法（直接「连接成功 · 读取到 型号」，
    /// 型号是表单里选的那个）。
    /// </summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(string host, int port, string token, string? demoModelKey, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            var m = (_demo?.Models ?? ModelProfiles.Embedded).Get(demoModelKey) ?? ModelProfiles.Embedded.All[0];
            return new ConnectionTestResult(true, "连接成功 · 读取到 " + m.Label, null, m);
        }
        return await ConnectionTester.TestAsync(host, port, token, ClientName, ModelProfiles.Embedded, _httpFactory, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 添加设备并开始连接（样稿 addDevice + 「添加并连接」）：演示 = DemoFleet.AddDevice（从未连上、2 s 后上线）；实时 = 登记（立即写 devices.json）+ 开始连接
    /// （不等连接结果：设备马上出现在列表里，状态「正在连接…」；连不上时设备留着，显示原因）。别名不合规时返回 null（对话框先拦住）。
    /// </summary>
    public DeviceVm? AddDevice(NewDevice n)
    {
        if (AliasError(n.Alias, null).Length > 0 || string.IsNullOrWhiteSpace(n.Host)) return null;
        if (_demo is { } demo)
        {
            var dd = demo.AddDevice(new DemoNewDevice(n.Host.Trim(), n.Port, n.Token, ActionGuard.JsTrim(n.Alias), n.AutoConnect, n.ModelKey));
            SyncDemoDevices();
            RefreshNow();
            return Device(dd.K);
        }
        if (Registry is not { } reg) return null;
        var rd = reg.Add(new DeviceEntry
        {
            Alias = n.Alias, Host = n.Host, Port = n.Port is > 0 and <= 65535 ? n.Port : ControlConfig.DefaultPort,
            Token = string.IsNullOrWhiteSpace(n.Token) ? null : n.Token.Trim(), RememberToken = n.RememberToken, AutoConnect = n.AutoConnect && n.RememberToken,
        });
        _ = reg.ConnectAsync(rd.Id);
        return Device(rd.K);
    }

    /// <summary>
    /// 编辑设置（样稿 editDevice）：返回 '' 或别名错误。实时：地址 / 端口 / 令牌变了且正连着时先释放控制权、断开，再按新设置连接；
    /// 「记住令牌」决定令牌是否落盘、「启动时自动连接」要求记住了令牌（D7）。<paramref name="background"/> = 存好就返回、重新连接在后台做（编辑设置对话框）。
    /// </summary>
    public async Task<string> EditDeviceAsync(int k, DeviceSettingsEdit e, bool background = false)
    {
        if (_demo is { } demo)
        {
            if (demo.Device(k) is not { } dd) return "设备不存在";
            var r = demo.EditDevice(dd, new DemoDeviceEdit(e.Alias, e.Host.Trim(), e.Port, e.Token, e.AutoConnect));
            RefreshNow();
            return r;
        }
        if (Registry?.ByK(k) is not { } rd) return "设备不存在";
        var res = await Registry.EditAsync(rd.Id, new DeviceEdit(e.Alias, e.Host, e.Port, e.Token, e.RememberToken, e.AutoConnect), background).ConfigureAwait(true);
        RefreshNow();
        return res;
    }

    /// <summary>
    /// 「启动时自动连接」开关（设备管理表）：返回 '' 或原因（实时：要求记住了令牌，D7）。演示同样稿（直接切换）。
    /// </summary>
    public string SetAutoConnect(int k, bool on)
    {
        if (_demo is { } demo)
        {
            if (demo.Device(k) is not { } dd) return "设备不存在";
            demo.SetAutoConnect(dd, on);
            return "";
        }
        if (Registry?.ByK(k) is not { } rd) return "设备不存在";
        var r = Registry.SetAutoConnect(rd.Id, on);
        RefreshNow();
        return r;
    }

    /// <summary>
    /// 这台设备能不能勾「启动时自动连接」：'' = 能；否则原因（实时，D7）—— 没勾「记住令牌」：「先勾选「记住令牌」…」；勾了、但令牌没有（记住的读不出来、
    /// 还没输入）：「令牌未保存 · 在「编辑设置」里输入」（M3 复核：以前这时也写「先勾选「记住令牌」」）。演示恒为 ''。
    /// </summary>
    public string AutoConnectReason(int k) => Registry?.ByK(k) is { } rd ? DeviceRegistry.AutoConnectReason(rd.Entry) : "";

    /// <summary>设备管理表「自动连接」那一列的勾（实时：启动时真的会自动连接才算 —— 勾了、记住了令牌、令牌也读得出来，DeviceEntry.CanAutoConnect）。</summary>
    public bool AutoConnectOf(int k) => _demo?.Device(k)?.AutoConnect ?? (Registry?.ByK(k)?.Entry is { } e && e.CanAutoConnect);

    // ———————————————————— 本机客户端名（实时） ————————————————————

    /// <summary>客户端名检查（本机客户端名对话框）：'' = 可用，否则原因（见 ControlConfig.ClientNameError）。</summary>
    public string ClientNameError(string? name) => ControlConfig.ClientNameError(name);

    /// <summary>
    /// 改本机客户端名（实时；样稿的「设置」页在第二阶段，之前从「设备 › 本机客户端名…」改，M3 复核）：返回 '' 或原因。写进 devices.json，菜单栏右端跟着变；
    /// 每台设备<b>下一次新建连接</b>时用新名字（已经建好的连接照旧用旧名字 —— HMI 认的持有者也是旧名字，断开再连之后才换）。演示数据不能改。
    /// </summary>
    public string SetClientName(string? name)
    {
        if (Config is not { } cfg) return "演示数据不能改本机客户端名";
        var err = ControlConfig.ClientNameError(name);
        if (err.Length > 0) return err;
        var n = ControlConfig.NormalizeClientName(name);
        if (n == ClientName) return "";
        cfg.ClientName = n;
        cfg.Save();
        ClientName = n;
        OnPropertyChanged(nameof(ClientName));
        foreach (var s in _sources.OfType<LiveDeviceSource>()) s.ClientName = n;
        RefreshNow();
        return "";
    }

    /// <summary>运行中保存 devices.json 失败（ControlConfig.SaveFailed）：提示一下（不然改动看起来成功了、下次启动却没了）。</summary>
    private void OnSaveFailed(string message) => RaiseToast(message, ToastKind.Warn);

    // ———————————————————— 释放 ————————————————————

    /// <summary>退出：释放全部数据源（实时：并行释放控制权，每台 ≤ 3 s、总共 ≤ 5 s，再摘掉事件、断开）。可在任意线程调用。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_demo != null)
        {
            _demo.Changed -= OnDemoChanged;
            _demo.Toast -= OnDemoToast;
        }
        if (Registry != null) Registry.Changed -= OnRegistryChanged;
        if (Config != null) Config.SaveFailed -= OnSaveFailed;
        var all = _sources.ToList();
        foreach (var s in all) DetachSource(s);
        if (Registry != null) await Registry.ShutdownAsync(DeviceRegistry.ReleasePerDevice, DeviceRegistry.ShutdownBudget).ConfigureAwait(false);
        else await Task.WhenAll(all.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}

/// <summary>「添加设备」表单的结果（样稿 addDevice 的 { host, port, token, alias, autoConnect, modelKey } + D7 的「记住令牌」；型号只对演示数据有用）。</summary>
public sealed record NewDevice(string Host, int Port, string Token, string Alias, bool AutoConnect, bool RememberToken, string? ModelKey = null);

/// <summary>「编辑设置」表单的结果（Token 为空 = 不改令牌）。</summary>
public sealed record DeviceSettingsEdit(string Alias, string Host, int Port, string? Token, bool RememberToken, bool AutoConnect);

/// <summary>一台设备的设置（编辑设置对话框的初值）。ModelText = 「HT-PR4 · 4 路」（编辑时型号只读）。</summary>
public sealed record DeviceSettings(int K, string Alias, string Host, int Port, string Token, bool RememberToken, bool AutoConnect, string ModelKey, string ModelText, string Sn);
