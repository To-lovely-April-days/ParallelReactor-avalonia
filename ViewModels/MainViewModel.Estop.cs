using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using ParallelReactor.Services;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 软件急停：一键并行下发 停加热（AI-8 Srun=9655 + 8 路 At=4）/ 停搅拌（DM2C 减速停车）/ 全关 12 路阀门（IO16R），
/// 同时中止气路自动序列、停止曲线与逼近、各通道转为已结束。之后锁定：拒绝一切启动类操作，
/// 未确认的部分每 3 个 Tick 自动重发，直到操作员确认复位。复位只解除锁定，设备保持停止。
/// <para>
/// 执行分两段（C22）：<see cref="RequestEstop"/> 是快速路径，任意线程同步调用——立即置服务层锁（温控 / 搅拌 / 阀门，volatile）、
/// 直接下发三路停止帧，不等 UI 线程；锁存文件随后在线程池写（刷盘可能很慢，不能挡在停止帧前面）；
/// 之后把完整的界面流程（<see cref="TriggerEstopAsync"/>：状态、事件、补发与自动重发）投递到 UI 线程。
/// </para>
/// <para>
/// 快速路径每一路都发两遍：先走总线紧急视图（插到已排队的普通事务之前，最快落地），再以普通优先级补一遍。
/// 服务层锁是在「发起写入时」判定的：按下急停前已通过判定、正排在普通队列里的启动 / 开阀帧，会落在紧急停止帧之后；
/// 普通优先级的第二遍按先来先得排在这些旧帧之后，保证最后落到设备上的是停止帧。快速路径的结果以第二遍为准。
/// 补发 / 自动重发一律普通优先级。所有轮次都带本次急停的取消令牌，每帧下发前检查：复位后不再排出新的停止帧（C18）。
/// </para>
/// <para>
/// 锁存（C23）：急停触发即写 <see cref="EstopLatch"/>（数据目录 estop.latch / 模拟 estop-sim.latch），只有本地复位才删除；
/// 开机发现锁存时直接进入急停锁定并下发一次停止。
/// </para>
/// <para>这是通讯层面的急停，不能替代设备上的硬件急停回路（软件读不到硬件急停的状态）。</para>
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _isEstopPanelOpen;
    [ObservableProperty] private bool _isEstopResetConfirmOpen;
    [ObservableProperty] private string _estopTimeText = "";
    [ObservableProperty] private string _estopResetWarn = "";

    public EstopRow EstopHeat { get; } = new("加热", "AI-8 全局停止 Srun=9655 + 8 路 At=4", "已停止");
    public EstopRow EstopStir { get; } = new("搅拌", "DM2C 减速停车", "已停止");
    public EstopRow EstopValves { get; } = new("阀门", "IO16R 12 路全关（SV1–8 · 三路气源 · 排空）", "已全关");
    private IEnumerable<EstopRow> EstopRows => new[] { EstopHeat, EstopStir, EstopValves };

    public string EstopButtonText => Estopped ? "急停中" : "急停";
    public bool HasEstopResetWarn => EstopResetWarn.Length > 0;
    public string EstopSummary
    {
        get
        {
            var bad = EstopRows.Where(r => r.Part is EstopPart.Failed).Select(r => r.Name).ToList();
            if (EstopRows.Any(r => r.Part == EstopPart.Pending)) return "正在下发停止指令…";
            return bad.Count == 0
                ? "停止指令已全部确认 · 设备保持停止，复位前不能重新运行"
                : $"{string.Join("、", bad)}停止未确认（通讯失败）· 约每 4 秒自动重发 · 必要时使用设备硬件急停";
        }
    }

    private int _estopInFlight;   // 正在进行的急停下发（触发 / 补发 / 重发）
    private int _estopRetryTick;

    /// <summary>快速路径已置锁、UI 流程可能还没跑到（任意线程写）。界面守卫按 <see cref="EstopLocked"/> 判。</summary>
    private volatile bool _estopFast;

    /// <summary>急停锁定（UI 状态已置，或快速路径已锁、UI 流程尚未跑到）。</summary>
    public bool EstopLocked => Estopped || _estopFast;

    // 快速路径下发的那一轮（UI 流程首轮直接等它的结果，不重复发帧）
    private readonly object _fastEstopLock = new();
    private Task<EstopResult>? _fastEstopTask;
    private long _fastEstopAt;

    /// <summary>本次急停的取消令牌（快速路径与界面流程共用）：复位时取消，在途 / 等待中的各轮不再排出新的停止帧（C18）。
    /// 由 <see cref="_fastEstopLock"/> 保护（快速路径可在任意线程换新）。</summary>
    private CancellationTokenSource? _estopCts;

    // 急停请求序号（任意线程 Interlocked 递增）与最近一次本地复位时看到的序号：
    // 复位与另一线程的急停请求交错时，用它判断请求是否落在复位之后（见 ConfirmEstopReset / TriggerEstopAsync）
    private long _estopReqSeq;
    private long _estopResetSeq;

    /// <summary>锁存文件的写 / 删互斥（后台写入与 UI 线程复位不交错）。</summary>
    private readonly object _latchLock = new();

    /// <summary>急停锁存文件（构造函数里按数据目录与是否模拟建立）。</summary>
    private EstopLatch _estopLatch = null!;

    partial void OnEstoppedChanged(bool value)
    {
        // 服务层兜底锁：界面守卫之外，已在途的启动流程也会在下一步写入前被拦下
        LockServices(value);
        if (!value) _estopFast = false;
        OnPropertyChanged(nameof(EstopButtonText));
    }

    private void LockServices(bool on)
    {
        Temp.Locked = on;
        _stir.Locked = on;
        _valves.Locked = on;
    }

    /// <summary>换一个新的急停令牌（取消上一轮：上一轮的补发等待 / 未发出的帧不再下发，新一轮会完整重发）。线程安全。</summary>
    private CancellationToken NewEstopToken()
    {
        CancellationTokenSource? old;
        var cts = new CancellationTokenSource();
        lock (_fastEstopLock)
        {
            old = _estopCts;
            _estopCts = cts;
        }
        old?.Cancel();   // 锁外取消：取消回调可能同步执行续体
        return cts.Token;
    }

    private CancellationToken CurrentEstopToken()
    {
        lock (_fastEstopLock) return _estopCts?.Token ?? new CancellationToken(true);
    }

    /// <summary>
    /// 急停快速路径（线程安全，可在任意线程调用，如将来的远程接口线程）：
    /// ① 同步置温控 / 搅拌 / 阀门服务锁，之后不会再发起任何启动 / 开阀写入；② 不经 UI 线程直接下发三路停止帧
    /// （紧急一遍 + 普通一遍，见类说明）；③ 在线程池写急停锁存文件；④ 把完整界面流程投递到 UI 线程（在 UI 线程上调用时直接执行）。
    /// </summary>
    /// <param name="source">来源（触屏 / 远程…），写入锁存与事件。</param>
    public void RequestEstop(string source)
    {
        long seq = Interlocked.Increment(ref _estopReqSeq);
        LockServices(true);
        _estopFast = true;
        _abortKeepVent = false;   // 急停一律全关（含排空），序列收尾不再保留排空阀
        // §5.3 第 4 条 / §7.3：急停（任何来源）一律收回控制权。复位之后远程须重新申请，原持有者不能直接再启动加热 / 加压
        bool tookBack = Arbiter.ReturnToLocal("急停");
        var ct = NewEstopToken();

        var task = EmergencyStop.RunAsync(
            FastHeatStopOp(ct),
            TimedEstopOp("搅拌", "快速路径", FastStirStopOp(ct), ct: ct),
            TimedEstopOp("阀门", "快速路径", FastValveCloseOp(ct), ct: ct));
        lock (_fastEstopLock)
        {
            _fastEstopTask = task;
            _fastEstopAt = Environment.TickCount64;
        }
        Logger.Event(DateTimeOffset.Now, "err", "系统", "急停快速路径",
            $"来源 {source} · 服务层已锁定 · 停止帧已直接下发（紧急优先一遍 + 普通优先级一遍）· 锁存文件后台写入"
            + (tookBack ? " · 远程控制权已收回（回到本地控制）" : ""));

        // 锁存在停止帧之后、线程池上写（Flush 到 SD / eMMC 可能要几十 ms 到秒级）
        _ = Task.Run(() => WriteEstopLatch(source));

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) _ = TriggerEstopAsync(source, seq: seq);
        else Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = TriggerEstopAsync(source, seq: seq));
    }

    /// <summary>写急停锁存（任意线程）。失败时记事件，并投递到 UI 线程报警 + 提示（重启后不会保持锁定）。</summary>
    private void WriteEstopLatch(string source)
    {
        try
        {
            lock (_latchLock) _estopLatch.Write(new LatchInfo(DateTimeOffset.Now, source));
            LogFileEvent("info", "系统", "急停锁存已写入", _estopLatch.Path);
        }
        catch (Exception ex)
        {
            Logger.Event(DateTimeOffset.Now, "err", "系统", "急停锁存写入失败", $"{_estopLatch.Path} · {ex.Message}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Alarm.Log("err", "系统", "急停锁存写入失败 · 重启后不会保持锁定",
                    $"{_estopLatch.Path} · {ex.Message} · 本次急停照常执行；复位前请勿断电重启，或检查数据目录磁盘");
                Toast("err", "急停锁存写入失败 · 重启后不会保持急停锁定");
            });
        }
    }

    /// <summary>取走 2 秒内由快速路径发起的那一轮（UI 首轮直接等它），没有则 null。</summary>
    private Task<EstopResult>? TakeFastEstop()
    {
        lock (_fastEstopLock)
        {
            var t = _fastEstopTask;
            _fastEstopTask = null;
            return t != null && Environment.TickCount64 - _fastEstopAt < 2000 ? t : null;
        }
    }

    // —— 快速路径三路：不碰任何界面对象（阀门结果由 UI 流程回填显示）。每路先紧急一遍、再普通一遍，结果以普通一遍为准 ——

    private Func<Task<bool>>? FastHeatStopOp(CancellationToken ct)
    {
        if (!HardwareOptions.UseRealTemp) return null;
        (bool Gate, bool Each)? u = null, n = null;
        return TimedEstopOp("加热", "快速路径", async () =>
        {
            var r1 = await Temp.EmergencyStopDetailedAsync(ct, urgent: true).ConfigureAwait(false);
            u = (r1.Gate, r1.Each);
            var r2 = await Temp.EmergencyStopDetailedAsync(ct, urgent: false).ConfigureAwait(false);
            n = (r2.Gate, r2.Each);
            return r2.Ok;
        }, () => (u is { } a ? $" · 紧急 Srun=9655 {(a.Gate ? "成功" : "失败")} / At=4 {(a.Each ? "全部成功" : "有失败")}" : " · 紧急一遍未完成")
               + (n is { } b ? $" · 普通 Srun=9655 {(b.Gate ? "成功" : "失败")} / At=4 {(b.Each ? "全部成功" : "有失败")}" : " · 普通一遍未完成"), ct);
    }

    private Func<Task<bool>>? FastStirStopOp(CancellationToken ct) => HardwareOptions.UseRealStir ? async () =>
    {
        await _stir.StopAsync(urgent: true).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return false;
        return await _stir.StopAsync(urgent: false).ConfigureAwait(false);
    } : null;

    private Func<Task<bool>>? FastValveCloseOp(CancellationToken ct) => RealIo ? async () =>
    {
        for (int i = 0; i < 2 && !ct.IsCancellationRequested; i++)
        {
            try { await _valves.WriteAllAsync(ValveFrame.AllClosed(), urgent: true).ConfigureAwait(false); break; }
            catch { await Task.Delay(100).ConfigureAwait(false); }
        }
        for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            try { await _valves.WriteAllAsync(ValveFrame.AllClosed()).ConfigureAwait(false); return true; }
            catch { await Task.Delay(200).ConfigureAwait(false); }
        }
        return false;
    } : null;

    /// <summary>
    /// 开机发现急停锁存（C23）：直接进入急停锁定（构造函数里调用，Alarm 已建立），
    /// 窗口起来后由 UI 流程下发一次停止。只有本地复位才会删除锁存。
    /// </summary>
    private void RestoreEstopLatch()
    {
        if (!_estopLatch.TryRead(out var info) || info == null) return;
        Estopped = true;   // OnEstoppedChanged 锁住三个服务
        EstopTimeText = $"触发时间 {info.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss}（上次运行）";
        IsEstopPanelOpen = true;
        foreach (var r in EstopRows) r.Part = EstopPart.Pending;
        Alarm.Log("err", "系统", "上次急停未复位，开机保持锁定",
            $"锁存 {_estopLatch.Path} · 来源 {info.Source} · 触发于 {info.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss} · 正在重新下发停止指令，需本地复位");
        NewEstopToken();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Toast("err", "上次急停未复位 · 开机保持急停锁定，复位后才能运行");
            _ = TriggerEstopAsync("开机恢复", restored: true);
        });
    }

    partial void OnEstopResetWarnChanged(string value) => OnPropertyChanged(nameof(HasEstopResetWarn));

    /// <summary>工具栏「急停」：任何时候按下都会（重新）下发全部停止指令并打开急停面板。不需要确认。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryEstop))]
    private void Estop() => Local(Ops.TryEstop(Src));

    /// <summary>急停的界面流程（UI 线程）。restored：开机恢复锁存（时间沿用上次、事件文字不同）。
    /// seq：发起它的 <see cref="RequestEstop"/> 的请求序号。</summary>
    private async Task TriggerEstopAsync(string source = "触屏", bool restored = false, long seq = 0)
    {
        bool first = !Estopped || restored;
        // 本次急停的令牌：快速路径已换新（再次按下时上一轮的补发等待随之取消，新一轮会完整重发）
        var ct = CurrentEstopToken();
        Estopped = true;   // 先锁：OnEstoppedChanged 同步锁住温控 / 搅拌 / 阀门服务（快速路径已锁，这里幂等）
        LockServices(true);
        _abortKeepVent = false;
        // 这次请求（在别的线程）先于一次本地复位发生、复位删掉了它写的锁存，但界面流程是在复位之后才跑到：
        // 设备重新进入急停，锁存也要补写，否则重启后不保持锁定（C23）
        if (!restored && seq > 0 && seq <= Interlocked.Read(ref _estopResetSeq))
        {
            if (ct.IsCancellationRequested) ct = NewEstopToken();   // 复位取消了它的令牌：本轮界面流程换新令牌
            _ = Task.Run(() => WriteEstopLatch(source));
        }
        IsEstopPanelOpen = true;
        IsEstopResetConfirmOpen = false;
        if (first && !restored) EstopTimeText = $"触发时间 {DateTime.Now:HH:mm:ss}";
        Alarm.Log("err", "系统",
            restored ? "开机恢复急停锁定 · 重新下发停止指令" : first ? "急停按下 · 停止指令下发" : "急停再次按下 · 重新下发",
            $"来源 {source} · 加热 / 搅拌 / 阀门三路并行下发，各部分回应时刻见事件日志");

        // —— 界面侧立即停：气路序列、程序执行、曲线、逼近、通道状态、搅拌开关、手动阀控、各确认弹窗 ——
        bool hadGas = GasBusy;
        AbortAllGas();   // 急停不看占用者：一律中止
        Program.AbortProgram();   // 程序执行中也中止（非气路步骤如等待/反应倒计时，靠程序自己的取消令牌）
        foreach (var c in Reactors)
        {
            if (c.Profile.Running) c.Profile.Stop();
            c.Approach.Stop();
            // 报警通道保持报警（C19）：急停把它转成「已结束」的话，复位后再开始运行就会给报警通道重新加热
            if (c.State is not (ReactorState.Idle or ReactorState.Alarm)) c.State = ReactorState.Done;
            c.Rpm = 0;
        }
        StirOn = false;        // Estopped 已为 true：setter 不另发停止帧，由下面统一下发并等结果
        ManualValve = false;
        IsPreRunOpen = false;
        Program.CancelPendingRun();   // 关确认框并丢弃待运行程序（确认时还会再判急停，双保险）
        Keyboard.IsOpen = false;   // 关掉键盘且不执行其回调
        UpdateRunCount();
        RefreshSchematic();
        Drawer.RaiseAll();

        // —— 硬件侧：三条独立总线并行下发 ——
        foreach (var r in EstopRows) r.Part = EstopPart.Pending;
        RaiseEstop();
        _estopInFlight++;
        try
        {
            // 首轮：快速路径刚下发过（2 秒内）就直接等它的结果，不重复发帧（少占总线、少写仪表 EEPROM）
            var fast = TakeFastEstop();
            var res = fast != null
                ? await fast
                : await EmergencyStop.RunAsync(
                    HeatStopTimed("首轮", ct),
                    TimedEstopOp("搅拌", "首轮", StirStopOp(), ct: ct),
                    TimedEstopOp("阀门", "首轮", ValveCloseOp(ct), ct: ct));
            if (ct.IsCancellationRequested) return;
            if (fast != null && res.Valves == EstopPart.Ok) ShowValves(ValveFrame.AllClosed());   // 快速路径不碰界面，这里回填
            SetEstopParts(res);
            if (first) LogEstopTriggered(res, restored);

            // 补发一轮：按下急停时可能有一帧启动 / 开阀写入已在串口队列里，会落在停止帧之后。
            // 等气路序列收尾（它的 finally 也会全关阀）并留出在途事务的时间，再完整下发一次。
            // 等待期间复位（令牌取消）：不再补发，避免把复位后刚启动的加热 / 搅拌又停掉（C18）
            var sw = Stopwatch.StartNew();
            try
            {
                while (Estopped && (hadGas && GasBusy || sw.ElapsedMilliseconds < 1500) && sw.ElapsedMilliseconds < 4000)
                    await Task.Delay(100, ct);
            }
            catch (OperationCanceledException) { return; }
            if (!Estopped || ct.IsCancellationRequested) return;
            var res2 = await EmergencyStop.RunAsync(
                HeatStopTimed("补发", ct),
                TimedEstopOp("搅拌", "补发", StirStopOp(), ct: ct),
                TimedEstopOp("阀门", "补发", ValveCloseOp(ct), ct: ct));
            if (!ct.IsCancellationRequested) SetEstopParts(res2);
        }
        finally { _estopInFlight--; }
    }

    /// <summary>急停锁定期间由 Tick 调用：每 3 个 Tick 重发一次未确认的部分（已确认的不重复写，避免磨损仪表 EEPROM）。</summary>
    private void EstopRetryTick()
    {
        if (!Estopped || _estopInFlight > 0 || ++_estopRetryTick % 3 != 0) return;
        if (EstopRows.Any(r => r.Part == EstopPart.Failed)) _ = RetryEstopAsync(CurrentEstopToken());
    }

    private async Task RetryEstopAsync(CancellationToken ct)
    {
        _estopInFlight++;
        try
        {
            // 三路各自独立重发（任务一创建就已开始，三条总线并行）；复位后（令牌取消）未开始的部分不再下发
            var h = EstopHeat.Part == EstopPart.Failed ? EmergencyStop.PartAsync(HeatStopTimed("自动重发", ct)) : null;
            var s = EstopStir.Part == EstopPart.Failed ? EmergencyStop.PartAsync(TimedEstopOp("搅拌", "自动重发", StirStopOp(), ct: ct)) : null;
            var v = EstopValves.Part == EstopPart.Failed ? EmergencyStop.PartAsync(TimedEstopOp("阀门", "自动重发", ValveCloseOp(ct), ct: ct)) : null;
            if (h != null) SetRetried(EstopHeat, await h, ct);
            if (s != null) SetRetried(EstopStir, await s, ct);
            if (v != null) SetRetried(EstopValves, await v, ct);
            RaiseEstop();
        }
        finally { _estopInFlight--; }
    }

    private void SetRetried(EstopRow row, EstopPart part, CancellationToken ct)
    {
        if (!Estopped || ct.IsCancellationRequested) return;   // 重发期间已复位：不再改面板
        row.Part = part;
        if (part == EstopPart.Ok)
            Alarm.Log("info", "系统", $"急停 · {row.Name}{row.OkText}", "自动重发后确认");
    }

    private void SetEstopParts(EstopResult r)
    {
        if (!Estopped) return;
        EstopHeat.Part = r.Heat;
        EstopStir.Part = r.Stir;
        EstopValves.Part = r.Valves;
        RaiseEstop();
    }

    private void RaiseEstop() => OnPropertyChanged(nameof(EstopSummary));

    private void LogEstopTriggered(EstopResult r, bool restored = false)
    {
        string detail = string.Join(" · ", EstopRows.Select(x => $"{x.Name} {x.StatusText}"));
        Alarm.Log("err", "系统", restored ? "开机恢复急停 · 停止指令结果" : "急停触发", detail);
        Toast("err", r.AllConfirmed
            ? "急停已触发 · 加热 / 搅拌已停止，阀门已全关 · 复位后才能重新运行"
            : "急停已触发 · 部分停止未确认，正在自动重发 · 必要时使用设备硬件急停");
    }

    // 未配置串口的模块软件从未控制过，记为「未配置」，不算失败
    private Func<Task<bool>>? StirStopOp() => HardwareOptions.UseRealStir ? _stir.StopAsync : null;

    /// <summary>加热停止 + 计时事件；事件里的 Srun / At=4 两步结果取自<b>本次</b>调用（多轮并发时不会串到别的轮次）。
    /// 普通优先级（首轮 / 补发 / 自动重发）；ct 每帧下发前检查。</summary>
    private Func<Task<bool>>? HeatStopTimed(string round, CancellationToken ct = default)
    {
        if (!HardwareOptions.UseRealTemp) return null;
        (bool Gate, bool Each)? d = null;
        return TimedEstopOp("加热", round, async () =>
        {
            var r = await Temp.EmergencyStopDetailedAsync(ct);
            d = (r.Gate, r.Each);
            return r.Ok;
        }, () => d is { } x
            ? $" · Srun=9655 {(x.Gate ? "成功" : "失败")} · 8 路 At=4 {(x.Each ? "全部成功" : "有失败")}"
            : " · 未完成（异常）", ct);
    }

    private Func<Task<bool>>? ValveCloseOp(CancellationToken ct) => RealIo ? () => CloseAllValvesAsync(report: false, estop: true, ct: ct) : null;

    /// <summary>急停面板「收起」：锁定保持，工具栏按钮显示「急停中」，再按会重新下发并打开面板。</summary>
    [RelayCommand]
    [ViewEntry("收起急停面板（锁定保持）")]
    private void CloseEstopPanel() => IsEstopPanelOpen = false;

    /// <summary>急停面板「复位」：弹确认框；有停止未确认时在确认框里提醒先到现场确认。</summary>
    [RelayCommand]
    [ViewEntry("打开急停复位确认框")]
    private void EstopReset()
    {
        var bad = EstopRows.Where(r => r.Part is EstopPart.Failed or EstopPart.Pending).Select(r => r.Name).ToList();
        EstopResetWarn = bad.Count == 0
            ? ""
            : $"注意：{string.Join("、", bad)}的停止指令尚未确认（通讯失败）。复位前请到设备现场确认已停止，必要时切断电源。";
        IsEstopResetConfirmOpen = true;
    }

    [RelayCommand]
    [ViewEntry("关闭急停复位确认框")]
    private void CancelEstopReset() => IsEstopResetConfirmOpen = false;

    /// <summary>急停复位确认框「确认」：关掉确认框和急停面板，经 TryResetEstop 复位（只允许本地）。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryResetEstop))]
    private void ConfirmEstopReset()
    {
        IsEstopResetConfirmOpen = false;
        IsEstopPanelOpen = false;
        Local(Ops.TryResetEstop(Src));
    }

    /// <summary>
    /// 急停复位（命令层 TryResetEstop 调用，不弹 Toast）：取消在途的停止帧、删除锁存、解除锁定（设备保持停止）。
    /// 复位途中另一线程又发起急停时立刻锁回去。
    /// </summary>
    internal OpResult ResetEstopCore()
    {
        bool unconfirmed = HasEstopResetWarn;
        // 先取消：快速路径 / 补发 / 重发里尚未排出的停止帧不会在复位之后再下发（C18）。
        // 已排进总线队列的那一帧（每条总线至多一帧）排在复位后任何新的启动帧之前，先落地
        CancellationTokenSource? cts;
        lock (_fastEstopLock)
        {
            cts = _estopCts;
            _estopCts = null;
            _fastEstopTask = null;
        }
        cts?.Cancel();
        long seenSeq = Interlocked.Read(ref _estopReqSeq);
        Interlocked.Exchange(ref _estopResetSeq, seenSeq);
        bool latchCleared;
        lock (_latchLock) latchCleared = _estopLatch.Clear();   // 只有本地复位才删除锁存（C23）
        Estopped = false;   // 只解除锁定：设备保持停止，需要时重新开始运行
        // 复位途中另一线程又发起了急停（RequestEstop 已置锁）：OnEstoppedChanged 刚解了锁，这里立刻锁回去，
        // 它投递的界面流程随后重新进入急停并补写锁存
        if (Interlocked.Read(ref _estopReqSeq) != seenSeq)
        {
            LockServices(true);
            _estopFast = true;
        }
        Alarm.Log("warn", "系统", "急停已复位",
            "操作员确认复位 · 设备保持停止" + (unconfirmed ? " · 复位时仍有停止指令未确认" : "")
            + (latchCleared ? " · 锁存已清除" : ""));
        if (!latchCleared)
        {
            Alarm.Log("err", "系统", "急停锁存文件删除失败 · 下次开机仍将锁定", _estopLatch.Path);
            // 复位本身已完成（Ok），只是锁存没删掉：按 err 提示
            return OpResult.Done("急停已复位，但锁存文件删除失败 · 下次开机仍将保持急停锁定", "err",
                new { reset = true, latchCleared = false, path = _estopLatch.Path });
        }
        return OpResult.Done("急停已复位 · 设备保持停止，需要时重新开始运行", "ok",
            new { reset = true, latchCleared = true, unconfirmed });
    }
}

/// <summary>急停面板的一行：某一部分的停止指令与下发结果。</summary>
public partial class EstopRow : ObservableObject
{
    public string Name { get; }
    public string Detail { get; }
    public string OkText { get; }
    [ObservableProperty] private EstopPart _part = EstopPart.Pending;

    public EstopRow(string name, string detail, string okText)
    {
        Name = name;
        Detail = detail;
        OkText = okText;
    }

    partial void OnPartChanged(EstopPart value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    public string StatusText => Part switch
    {
        EstopPart.Pending => "下发中…",
        EstopPart.Ok => OkText,
        EstopPart.Failed => "未确认 · 重发中",
        _ => "未配置串口",
    };

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(Part switch
    {
        EstopPart.Ok => "#5fae14",
        EstopPart.Failed => "#e0394c",
        EstopPart.Pending => "#c9820f",
        _ => "#7d8694",
    }));
}
