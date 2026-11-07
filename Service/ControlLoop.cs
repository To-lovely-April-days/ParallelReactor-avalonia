using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Services;

/// <summary>预热宿主（MainView）：预热期间被关闭则提前结束；结束时收起开机遮罩。</summary>
public interface IWarmupHost
{
    bool IsClosing { get; }
    void HideSplash();
}

/// <summary>
/// 控制循环（C1）：持有 Tick（1.2 s）、ClockTick（1 s）与开机预热（250 ms）三个 DispatcherTimer（UI 线程）。
/// 在 App 启动时创建，与 View 是否加载无关——DRM 下 View 卸载后循环照常运行，安全监视（超温 / 超压联锁、
/// 急停补发、运行记录）不依赖 View。退出时由 App / ConfirmExit 调用 <see cref="Stop"/>。
/// </summary>
public sealed class ControlLoop : IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1200);
    public static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan WarmInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>预热时逐个切换的主页签（最后停在首页再进入设置子页阶段，与原 MainView 一致）。</summary>
    private static readonly string[] WarmTabs = { "program", "graph", "data", "alarm", "setting", "home" };

    private readonly MainViewModel _vm;
    private DispatcherTimer? _tick;
    private DispatcherTimer? _clock;
    private DispatcherTimer? _warm;
    private IEnumerator<bool>? _warmSteps;
    private IWarmupHost? _warmHost;
    private bool _warmStarted;
    private bool _stopped;
    private int _snapFailLogged;

    public ControlLoop(MainViewModel vm)
    {
        _vm = vm;
        Gate = new TickGate(TickInterval);
    }

    /// <summary>防重入与超时统计（诊断 / /device/comm 使用）。</summary>
    public TickGate Gate { get; }

    /// <summary>每个 Tick 结束后发布的只读快照（对外接口的唯一数据源，设计稿 §2.2 第 4 条）。</summary>
    public Api.SnapshotStore Snapshots { get; } = new();

    /// <summary>遥测帧序号：每 Tick 加 1，开机从 0 起（设计稿 §8.3）。</summary>
    public long SnapshotSeq => _snapSeq;
    private long _snapSeq;

    public bool Running => _tick?.IsEnabled == true;

    /// <summary>创建并启动 Tick 与时钟定时器。只启动一次；Stop 之后不再重启。</summary>
    public void Start()
    {
        if (_stopped || _tick != null) return;

        // 实时数据 tick（1.2s，对应 HTML setInterval 节奏）
        _tick = new DispatcherTimer { Interval = TickInterval };
        _tick.Tick += (_, _) => RunTick();
        _tick.Start();

        // 时钟（1s）
        _clock = new DispatcherTimer { Interval = ClockInterval };
        _clock.Tick += (_, _) => _vm.ClockTick();
        _clock.Start();
    }

    /// <summary>
    /// 执行一次 Tick（经门闸防重入）。异常照旧冒泡到 Dispatcher（策略待决，见设计稿 §9 第 10 条）。
    /// finally 里的超时记录自己吞掉异常：不能让写日志的异常顶替 Tick 的原始异常（丢掉真正的堆栈）。
    /// </summary>
    public void RunTick()
    {
        if (!Gate.TryEnter()) return;
        var sw = Stopwatch.StartNew();
        bool ok = false;
        try { _vm.Tick(); ok = true; }
        finally
        {
            if (Gate.Exit(sw.Elapsed, DateTime.UtcNow))
            {
                try
                {
                    _vm.LogFileEvent("warn", "系统", "控制循环超时",
                        $"本次 Tick 耗时 {sw.Elapsed.TotalMilliseconds:0} ms（节拍 {TickInterval.TotalMilliseconds:0} ms）· 累计超时 {Gate.Overruns} 次 · 跳过重入 {Gate.Skipped} 次");
                }
                catch { /* 记录失败不影响控制循环，也不覆盖 Tick 本身的异常 */ }
            }
            // 快照在 Gate.Exit 之后生成（门闸统计已是本 Tick 的终值）；Tick 抛异常时不发布半套状态。
            // 注意：因此 /device/comm.tick 的 lastTookMs / overruns 不含快照生成（约 8×16 段记录的分配）与 Published 回调的耗时，
            // 门闸数字略低估每 Tick 的 UI 线程占用；验收指标以遥测 CSV 行间距为准。
            // 发布只做一次 volatile 写 + 非阻塞入队（WsHub），绝不等待网络。失败不覆盖 Tick 的异常。
            if (ok)
            {
                try { Snapshots.Publish(Api.Hmi.SnapshotBuilder.Build(_vm, ++_snapSeq, Gate, HardwareOptions.InstalledChannels)); }
                catch (Exception ex)
                {
                    if (_snapFailLogged++ % 50 == 0)
                        try { _vm.LogFileEvent("err", "接口", "快照生成失败", ex.GetBaseException().Message); } catch { }
                }
            }
        }
    }

    /// <summary>停止全部定时器（含预热）。可重复调用。</summary>
    public void Stop()
    {
        _stopped = true;
        _tick?.Stop();
        _clock?.Stop();
        StopWarm();
    }

    public void Dispose() => Stop();

    // ============ 启动预热：开机时把各页各渲染一次（JIT + 布局预热），消除首次进菜单卡顿 ============

    /// <summary>View 的 Loaded 中调用；只运行一次。没有 View（无头运行）时不预热。</summary>
    public void StartWarmup(IWarmupHost host)
    {
        if (_warmStarted) return;
        _warmStarted = true;
        _warmHost = host;
        if (_stopped) { host.HideSplash(); return; }
        _warmSteps = WarmSequence().GetEnumerator();
        _warm = new DispatcherTimer { Interval = WarmInterval };
        _warm.Tick += (_, _) => WarmStep();
        _warm.Start();
    }

    private void WarmStep()
    {
        if (_warmHost == null || _warmSteps == null) return;
        if (_warmHost.IsClosing || !_warmSteps.MoveNext()) FinishWarm();
    }

    /// <summary>预热序列：每拍一步，与原 MainView.WarmStep 的页序完全相同。</summary>
    private IEnumerable<bool> WarmSequence()
    {
        // 阶段一：逐个切 tab，让每页首次布局/渲染发生（被遮罩盖住，用户看不到）
        foreach (var tab in WarmTabs)
        {
            _vm.SwitchTab(tab);
            yield return true;
        }

        // 阶段二：停在设置页，逐个切设置子页预热——把「首次点各设置子页」的 JIT/实例化开销
        // 全部前置到开机遮罩期间，消除运行时点设置左菜单的一次性卡顿。
        _vm.SwitchTab("setting");   // 设置页可见，子页内容才会真正 realize
        yield return true;
        var pages = _vm.Setting.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            _vm.Setting.Selected = pages[i];
            yield return true;
        }
    }

    private void FinishWarm()
    {
        StopWarm();
        if (_vm.Setting.Pages.Count > 0) _vm.Setting.Selected = _vm.Setting.Pages[0];   // 复位到首个设置子页
        _vm.SwitchTab("home");   // 预热结束停在首页
        var host = _warmHost;
        _warmHost = null;
        host?.HideSplash();
    }

    private void StopWarm()
    {
        _warm?.Stop();
        _warm = null;
        _warmSteps = null;
    }
}
