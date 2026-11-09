using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Client;
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
/// <item>实时（<see cref="CreateLive"/>）：M2 只有一台（<see cref="Live"/>，「连接设备」对话框 → <see cref="ConnectAsync"/>）；多台、重连监督、设备管理在 M3。</item>
/// </list>
/// 刷新：数据源的变化、操作结果、每秒（<see cref="OnTick"/>，UiTicker）都只是<b>请求</b>刷新（<see cref="RequestRefresh"/>），排一次到 UI 线程，
/// 合并后就地刷新全部 DeviceVm，再触发一次 <see cref="Changed"/>（外框跟着刷新）。全部成员只在 UI 线程上用。
/// </summary>
public sealed partial class FleetVm : ObservableObject, IFleetView, IAsyncDisposable
{
    private readonly Action<Action> _post;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<ClientOptions, IReactorConnection>? _connFactory;
    private readonly List<IDeviceSource> _sources = new();
    private DemoFleet? _demo;
    private bool _refreshPending, _disposed, _persistConfig = true;

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
        f._demo = new DemoFleet(p ?? DemoParams.Default);
        f.Params = f._demo.Params;
        f._demo.Changed += f.OnDemoChanged;
        f._demo.Toast += f.OnDemoToast;
        f.SyncDemoDevices();
        f.RefreshNow();
        return f;
    }

    /// <summary>实时：开始时没有设备（「设备 › 连接…」打开「连接设备」对话框连上一台）。</summary>
    /// <param name="persistConfig">连上后把地址等写回 control.json（测试传 false：不写任何文件）。</param>
    public static FleetVm CreateLive(ControlConfig config, Action<Action>? post = null, SynchronizationContext? uiContext = null,
        Func<ClientOptions, IReactorConnection>? connFactory = null, bool persistConfig = true)
    {
        var f = new FleetVm(false, config.ClientName, post, uiContext, connFactory) { Config = config };
        f._persistConfig = persistConfig;
        return f;
    }

    // ———————————————————— IFleetView ————————————————————

    public string ClientName { get; private set; }
    public bool IsDemo { get; }
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

    // ———————————————————— 实时（M2：一台） ————————————————————

    /// <summary>本机配置（实时；演示为 null）。</summary>
    public ControlConfig? Config { get; private set; }

    /// <summary>当前的实时设备（M2 只有一台；没有为 null）。</summary>
    public LiveDeviceSource? Live => _sources.OfType<LiveDeviceSource>().FirstOrDefault();

    /// <summary>实时设备的连接（性能测试、程序页、导出用）；没有连上为 null。</summary>
    public IReactorConnection? LiveConnection => Live?.Connection;

    /// <summary>实时设备的视图模型。</summary>
    public DeviceVm? LiveDevice => DeviceList.FirstOrDefault(d => d.Source is LiveDeviceSource);

    /// <summary>
    /// 连接一台设备（临时的「连接设备」对话框；M2 只有一台：已有的先断开）：成功后加进设备列表（1号机）、保存地址（令牌按「记住令牌」），
    /// 失败时抛出（对话框显示「连接失败：…」，不留下连不上的设备 —— 重连监督在 M3）。
    /// </summary>
    public async Task<DeviceVm> ConnectAsync(string host, int port, string token, string clientName, bool rememberToken, CancellationToken ct = default)
    {
        if (IsDemo) throw new InvalidOperationException("演示数据不连接设备");
        var cfg = Config!;
        if (Live is { } old) await RemoveLiveAsync(old).ConfigureAwait(true);
        var dc = new DeviceConfig(Guid.NewGuid(), "1号机", host.Trim(), port, token.Trim(), cfg.AutoConnect);
        var src = new LiveDeviceSource(dc, clientName.Trim(), _post, _connFactory, _uiContext);
        try
        {
            await src.ConnectAsync(ct).ConfigureAwait(true);
        }
        catch
        {
            await src.DisposeAsync().ConfigureAwait(true);
            throw;
        }
        ClientName = clientName.Trim();
        OnPropertyChanged(nameof(ClientName));
        cfg.Host = dc.Host; cfg.Port = port; cfg.ClientName = ClientName; cfg.RememberToken = rememberToken; cfg.Token = dc.Token;
        if (_persistConfig) cfg.Save();
        Attach(src);
        var vm = new DeviceVm(this, 1, src);
        DeviceList.Add(vm);
        RefreshNow();
        return vm;
    }

    /// <summary>实时设备的本机操作（改名、移除；不发给 HMI）。</summary>
    internal async Task<ActionOutcome> LocalActionAsync(DeviceVm d, DeviceAction action, ActionPayload p)
    {
        if (d.Source is not LiveDeviceSource live) return ActionOutcome.Failed("设备不存在");
        if (action == DeviceAction.Rename)
        {
            var old = d.Alias;
            live.Rename(ActionGuard.JsTrim(p.Alias ?? ""));
            return ActionOutcome.Done(live.Config.Alias + " · 已重命名（原名 " + old + "）");
        }
        var alias = d.Alias;
        await RemoveLiveAsync(live).ConfigureAwait(true);
        return ActionOutcome.Done("已移除 " + alias);
    }

    private async Task RemoveLiveAsync(LiveDeviceSource live)
    {
        DetachSource(live);
        var vm = DeviceList.FirstOrDefault(x => ReferenceEquals(x.Source, live));
        if (vm != null) DeviceList.Remove(vm);
        await live.DisposeAsync().ConfigureAwait(true);
        RefreshNow();
    }

    // ———————————————————— 释放 ————————————————————

    /// <summary>退出：释放全部数据源（实时：释放控制权 ≤ 3 s、摘掉事件、断开）。可在任意线程调用。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_demo != null)
        {
            _demo.Changed -= OnDemoChanged;
            _demo.Toast -= OnDemoToast;
        }
        var all = _sources.ToList();
        foreach (var s in all) DetachSource(s);
        await Task.WhenAll(all.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
