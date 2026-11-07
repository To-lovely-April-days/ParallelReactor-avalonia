using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Commands;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 气路自动序列（程序「吹扫」步骤 / 主界面泄漏测试）的公共底层：占用与中止、整帧写阀并同步界面、
/// 即时读压力、保持阀态（带 IO16R 心跳）。同一时间只允许一个序列占用气路；多程序并行时程序之间经 <see cref="GasQueue"/> 排队轮流占用。
/// 阀门模块未配置、或需要压力时 8AI 离线，序列一律拒绝启动（不做任何模拟）。
/// </summary>
public partial class MainViewModel
{
    /// <summary>当前占用气路的自动序列名（如「RV1 程序」「泄漏测试」），空 = 空闲。</summary>
    [ObservableProperty] private string _gasTask = "";
    public bool GasBusy => GasTask.Length > 0;
    partial void OnGasTaskChanged(string value)
    {
        OnPropertyChanged(nameof(GasBusy));
        OnPropertyChanged(nameof(AnyRunning));   // 序列进行中：工具栏显示「全部停止」（可中止序列）
        OnPropertyChanged(nameof(NotRunning));
    }

    /// <summary>
    /// 手动阀操作的守卫：急停锁定中、自动序列进行中（阀门由序列控制）、或有程序在稳压 / 排队等待气路
    /// （脉冲间隙气路名义上空闲，但下一脉冲会整帧写阀覆盖手动阀态，§7.12 第 1 条），拒绝手动操作。null = 放行。
    /// </summary>
    internal OpResult? GasLockedForManual()
    {
        if (Estopped) return OpResult.Fail(OpCodes.EstopActive, "急停状态下禁止阀门操作 · 请先复位");
        if (GasBusy) return OpResult.Fail(OpCodes.GasBusy, $"{GasTask}进行中 · 阀门由自动序列控制", "warn");
        if (Program.ProgramGasPending is { } pending)
            return OpResult.Fail(OpCodes.GasBusy, $"{pending}进行中 · 阀门由程序控制", "warn");
        return null;
    }

    private static readonly Stopwatch _mono = Stopwatch.StartNew();
    /// <summary>单调时间（开机起流逝量）：程序等待气路的计时用它，不受 NTP 校时跳变影响。</summary>
    internal static TimeSpan MonoNow => _mono.Elapsed;

    /// <summary>最近一次 <see cref="AbortAllGas"/> 的来由（超压联锁 / 退出程序）；新租约开始时清零。程序引擎据此归类租约被取消的原因。</summary>
    internal GasAbortCause LastGasAbortCause { get; private set; }

    /// <summary>该通道正参与当前气路序列（停止通道时据此报告被中止的序列，H9）。</summary>
    internal bool GasIncludes(int id) => _lease.Includes(id);

    private CancellationTokenSource? _gasCts;

    /// <summary>当前租约的取消令牌（BeginGas 之后立即读取；程序引擎排队取租约后据此建链接令牌）。</summary>
    public CancellationToken GasToken => _gasCts?.Token ?? default;

    /// <summary>程序气路步骤的等待队列（多程序并行：先到先得，见 <see cref="Models.GasQueue"/>）。</summary>
    internal readonly GasQueue GasQueue = new();

    /// <summary>
    /// 开始自动序列前的公共检查：返回 null 表示可以开始，否则为拒绝原因。
    /// needPressure：序列是否依赖压力读数（泄漏测试要闭环充压与超压监视；吹扫只动总管、SV 全关，不依赖）。
    /// selfId：发起序列的程序自己的通道——它处于升压 / 反应不算「被占用」（连续升压、反应后补压）；报警仍然阻塞。
    /// programWait：发起方是程序的气路步骤——气路被另一程序占用不算阻塞（排队等待），其他正在执行程序的通道处于升压 / 反应也不算。
    /// </summary>
    public string? GasBlockReason(bool needPressure = true, int? selfId = null, bool programWait = false) => GasBlock(needPressure, selfId, programWait)?.Message;

    /// <summary>同 <see cref="GasBlockReason"/>，带 §11 错误码（命令层用，见 <see cref="GasGuard"/>）。</summary>
    public (string Code, string Message)? GasBlock(bool needPressure = true, int? selfId = null, bool programWait = false) => GasGuard.Check(new GasGuardInput
    {
        EstopLocked = EstopLocked,
        IoConfigured = RealIo,
        NeedPressure = needPressure,
        AnalogConfigured = Services.HardwareOptions.UseRealAnalog,
        PressOnline = _press.Online,
        GasTask = GasTask,
        ManualValve = ManualValve,
        SelfId = selfId,
        Channels = Reactors.Select(r => new GasChannel(r.Id, r.State, IsOverPressureTripped(r.Id), IsIdleOverTempAlarm(r))).ToList(),
        GasHeldByProgram = GasOwnedBy(GasOwner.Program),
        WaitInsteadOfBusy = programWait,
        ProgramOwnedIds = programWait ? Program.RunningIds.Where(x => x != selfId).ToList() : null,
        ProgramGasPending = Program.ProgramGasPending,
    });

    /// <summary>气路占用登记（占用者 / 参与通道 / 超压监视通道，见 <see cref="GasLease"/>）。</summary>
    private readonly GasLease _lease = new();

    /// <summary>当前程序租约所属通道（泄漏测试占用 / 空闲为 null）：程序引擎的「等待气路（RVx 占用中）」文字用。</summary>
    public int? GasHolderRv => _lease.OwnerRv;

    /// <summary>序列期间做超压监视的 RV（会开 SV 充压的通道）。</summary>
    private int[] GasWatch => _lease.Watch;

    /// <summary>
    /// 占用气路，返回本次序列的取消令牌（占用者自己的中止 / 全部停止 / 急停 / 超压联锁会触发取消）。
    /// participants：参与本序列的通道（程序所在通道、泄漏测试被测通道）——它们被停止 / 停用 / 报警时序列中止（C9）。
    /// watchIds：序列期间按压力做超压监视的 RV（会开 SV 充压的通道；吹扫不开 SV，为空）。
    /// 同一占用者可续租：重新登记，换新令牌与参与 / 监视通道。ownerRv：程序租约所属通道（多程序并行时按通道隔离中止 / 释放）。
    /// </summary>
    public CancellationToken BeginGas(GasOwner owner, string task, int[]? participants = null, int[]? watchIds = null, int? ownerRv = null)
    {
        _gasCts?.Dispose();
        _gasCts = new CancellationTokenSource();
        LastGasAbortCause = GasAbortCause.None;
        var ids = participants ?? Array.Empty<int>();
        var start = ids.Select(FindReactor).Where(r => r != null).ToDictionary(r => r!.Id, r => r!.State);
        _lease.Begin(owner, task, ids, watchIds, start, ownerRv);
        GasTask = task;
        return _gasCts.Token;
    }

    /// <summary>
    /// 释放气路（序列的 finally 里、全关阀之后调用）。只有当前占用者就是 owner 时才释放（C8）：
    /// 程序收尾不能释放泄漏测试的占用，反之亦然。gen ≠ 0 时还要求是同一段租约（见 <see cref="GasGeneration"/>）。
    /// </summary>
    public void EndGas(GasOwner owner, long gen = 0)
    {
        if (!_lease.TryEnd(owner, gen)) return;
        _abortKeepVent = false;
        _gasCts?.Dispose();
        _gasCts = null;
        GasTask = "";
    }

    /// <summary>当前气路是否由 owner 占用（gen ≠ 0 时还要求是同一段租约）。</summary>
    public bool GasOwnedBy(GasOwner owner, long gen = 0)
        => _lease.Owner == owner && owner != GasOwner.None && (gen == 0 || gen == _lease.Generation);

    /// <summary>当前租约代号（BeginGas 之后立即读取；后台回路收尾时据此只释放自己那段租约）。</summary>
    public long GasGeneration => _lease.Generation;

    /// <summary>
    /// 按占用者中止（C8）：只有当前占用者就是 owner 才取消（序列自己的 finally 负责全关阀）。
    /// 泄漏测试面板的「中止」不会停掉程序的吹扫 / 升压，程序的「中止」也不会停掉泄漏测试。返回是否发出了取消。
    /// rv 非 null：还要求是该通道程序的租约（程序 A 的中止不波及程序 B 的气路步骤）。
    /// </summary>
    public bool AbortGas(GasOwner owner, int? rv = null)
    {
        if (!_lease.CanAbort(owner, rv)) return false;
        _gasCts?.Cancel();
        return true;
    }

    /// <summary>
    /// 无条件中止任何占用者的气路序列：超压联锁、急停、全部停止、退出程序用。
    /// 序列收到取消后自己的 finally 全关阀并释放占用。
    /// </summary>
    public void AbortAllGas() => _gasCts?.Cancel();

    /// <summary>同 <see cref="AbortAllGas"/>，并记下来由（程序引擎据此区分「超压联锁」与「退出程序」）。</summary>
    public void AbortAllGas(GasAbortCause cause)
    {
        if (GasBusy) LastGasAbortCause = cause;
        _gasCts?.Cancel();
    }

    /// <summary>
    /// 中止并等序列收尾（其 finally 全关阀后才释放气路），最多等 timeoutMs，再兜底全关一次。退出程序前调用：
    /// 序列可能还有一帧开阀写在串口队列里，只发全关就退出的话，最后落到 IO16R 的可能是那帧开阀。
    /// </summary>
    public async Task AbortGasAndWaitAsync(int timeoutMs = 3000)
    {
        AbortAllGas(GasAbortCause.Shutdown);
        var sw = Stopwatch.StartNew();
        while (GasBusy && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        await CloseAllValvesAsync();
    }

    /// <summary>
    /// 通道被抽屉「停止 / 停用」时调用（C9）：该通道正在参与气路序列（或正被程序执行）的，
    /// 先按占用者干净地中止那个序列（序列自己的 finally 全关阀、释放占用），最多等 timeoutMs。
    /// 返回是否中止了该通道上的程序（调用方据此跳过重复的停加热下发）。
    /// </summary>
    public async Task<bool> ReleaseChannelFromGasAsync(int id, int timeoutMs = 3000)
    {
        _lease.Invalidate(id);   // 即使取消晚到一步，序列下一次安全检查也会因此中止
        bool program = Program.IsProgramOwned(id) && Program.AbortProgram(id);   // 程序会按本通道的占用者中止自己的气路（排队中的由令牌取消出队）
        if (!program && _lease.Includes(id)) AbortGas(_lease.Owner);
        var sw = Stopwatch.StartNew();
        while (_lease.Includes(id) && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        return program;
    }

    /// <summary>
    /// 序列运行中的安全检查：急停 / 任一釜报警 / 被监视通道超压 / 参与通道被停止、停用或报警（C9）时抛出，序列中止并全关阀。
    /// 被监视通道按压力直接判超压（Tick 只给运行中的釜置报警态，泄漏测试的釜多为空闲态）。
    /// </summary>
    public void ThrowIfGasUnsafe()
    {
        if (Estopped) throw new GasFaultException("急停已触发");
        var watch = GasWatch;
        bool program = _lease.Owner == GasOwner.Program;
        // 停用 / 已结束通道的超温报警只在该通道参与本序列（充压监视）时才中止。
        // 程序租约（多程序并行，§7.12 第 7 条）：不参与本序列的其他通道报警，只要不是超压未解除就不中止本程序的气路步骤
        // ——超温不涉及总管，只中止该通道自己的程序；超压（任一通道）仍一律中止。泄漏测试租约逐字同原来（任一釜报警即中止）。
        var a = Reactors.FirstOrDefault(r => r.State == ReactorState.Alarm
            && !(IsIdleOverTempAlarm(r) && !watch.Contains(r.Id))
            && !(program && !watch.Contains(r.Id) && !_lease.Participants.Contains(r.Id) && !IsOverPressureTripped(r.Id)));
        if (a != null) throw new GasFaultException($"RV{a.Id} 报警");
        var o = Reactors.FirstOrDefault(r => watch.Contains(r.Id) && r.P >= Settings.OverPressure);
        if (o != null) throw new GasFaultException($"RV{o.Id} 超压（{Units.FormatP(o.P)}）");
        if (_lease.FaultReason(id => FindReactor(id)?.State) is { } why) throw new GasFaultException(why);
    }

    /// <summary>
    /// 整帧写 12 路阀门（见 <see cref="ValveFrame"/>）并同步界面。不安全的阀态直接拒绝；
    /// 真机写失败重试 2 次，仍失败抛 <see cref="GasFaultException"/>。
    /// </summary>
    public async Task ApplyValvesAsync(bool[] frame, CancellationToken ct = default)
    {
        if (!ValveFrame.IsSafe(frame))
            throw new GasFaultException("阀态不安全（多路气源同开，或气源与排空同开），已拒绝下发");
        // 多程序并行的物理前提（§7.12 第 4 条）：一个序列只能打开自己参与通道的 SV，其他通道（可能正处于高压反应）的 SV 必须保持关闭
        if (_lease.Busy)
            for (int i = 0; i < 8 && i < frame.Length; i++)
                if (frame[i] && !_lease.Participants.Contains(i + 1))
                    throw new GasFaultException($"阀态越权：SV{i + 1} 不属于「{_lease.Task}」的参与通道，已拒绝下发");
        await WriteValvesAsync(frame, ct);
        ct.ThrowIfCancellationRequested();
        ShowValves(frame);
    }

    /// <summary>只写硬件（重试 2 次），不动界面。调用方保证 frame 已通过 <see cref="ValveFrame.IsSafe"/>。</summary>
    private async Task WriteValvesAsync(bool[] frame, CancellationToken ct)
    {
        if (!RealIo) throw new GasFaultException("阀门模块未配置串口（PR_IO_PORT）");
        Exception? err = null;
        for (int i = 0; i < 3; i++)
        {
            // 每次下发前都查一次取消：「全部停止」已发出全关后，不能再把开阀帧写下去
            ct.ThrowIfCancellationRequested();
            try { await _valves.WriteAllAsync(frame); err = null; break; }
            catch (Services.ValveLockedException) { throw new GasFaultException("急停锁定，拒绝开阀"); }
            catch (Exception ex) { err = ex; await Task.Delay(120); }
        }
        if (err != null) throw new GasFaultException($"阀门写入失败（IO16R 通讯）：{err.Message}");
    }

    /// <summary>把一帧阀态同步到界面（RV 进气阀 / 气源 / 排空）。</summary>
    private void ShowValves(bool[] f)
    {
        LogValveFrame(f);   // 与界面当前阀态比，有变化才记（事件文件）
        for (int i = 0; i < 8 && i < Reactors.Count; i++) Reactors[i].Valve = f[i];
        for (int g = 0; g < 3 && g < GasInlets.Count; g++) GasInlets[g].On = f[ValveFrame.GasBase + g];
        VentOn = f[ValveFrame.Vent];
        RefreshSchematic();
        if (Drawer.IsOpen) Drawer.RaiseAll();
    }

    /// <summary>
    /// 超压联锁中止气路序列时置位（C7「排空阀保持原样」）：序列 / 稳压收尾的 <see cref="CloseAllValvesAsync"/>
    /// 只关线圈 0-10（SV 与气源），不碰排空阀——否则排空阶段超压时，收尾会把正在泄压的排空阀关掉。
    /// 序列释放气路（<see cref="EndGas"/>）或急停时清除；急停一律全关（含排空）。
    /// </summary>
    private volatile bool _abortKeepVent;

    /// <summary>
    /// 尽力全关 12 路阀门（序列结束 / 中止时调用，不抛异常）。返回是否确认写入成功。
    /// report=false 时失败不弹提示、不记事件（急停面板自己显示结果并自动重发）。
    /// 超压中止（<see cref="_abortKeepVent"/>）期间、且不是急停调用（estop=false）时，只关 SV 与气源，排空阀保持。
    /// ct：每次重试前检查（急停复位后不再补关）。
    /// </summary>
    public async Task<bool> CloseAllValvesAsync(bool report = true, bool estop = false, CancellationToken ct = default)
    {
        var f = ValveFrame.AllClosed();
        bool keepVent = !estop && _abortKeepVent;
        if (keepVent) f[ValveFrame.Vent] = VentOn;   // 界面上排空阀保持原样（硬件上线圈 11 根本不写）
        if (RealIo)
        {
            bool ok = false;
            for (int i = 0; i < 4 && !ok; i++)
            {
                if (ct.IsCancellationRequested) return false;
                try
                {
                    if (keepVent) await _valves.CloseRangeAsync(0, ValveFrame.Vent);   // 线圈 0-10 全关，排空不写
                    else await _valves.WriteAllAsync(f);
                    ok = true;
                }
                catch { await Task.Delay(200); }
            }
            if (!ok)
            {
                // 写不进去就不改界面：显示的仍是最后一次确认的阀态
                if (report)
                {
                    Toast("err", "阀门全关写入失败（IO16R 通讯）· 请立即手动关闭气源");
                    Alarm.Log("err", "系统", "阀门全关写入失败", "IO16R 通讯异常，阀门实际状态未知，请手动关闭气源");
                }
                return false;
            }
        }
        ShowValves(f);
        return true;
    }

    /// <summary>
    /// 保持阀态 sec 秒（可取消）。每秒检查一次急停 / 报警，真机上还会重写一次当前帧（只写硬件、不刷界面）：
    /// IO16R 若设为「总线超时复位」，长时间无通讯会让线圈复位——常开阀（PR_IO_ENERGIZE=close）时等于全开，
    /// 所以平衡 / 测量这类长时间全关的阶段也必须保持心跳。
    /// </summary>
    public async Task HoldAsync(bool[] frame, double sec, CancellationToken ct)
    {
        if (!ValveFrame.IsSafe(frame))
            throw new GasFaultException("阀态不安全（多路气源同开，或气源与排空同开），已拒绝下发");
        var sw = Stopwatch.StartNew();
        double left;
        while ((left = sec - sw.Elapsed.TotalSeconds) > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1.0, left)), ct);
            ThrowIfGasUnsafe();
            if (sec - sw.Elapsed.TotalSeconds > 0.05) await WriteValvesAsync(frame, ct);
        }
    }

    /// <summary>
    /// 立即读一次 8 路压力（psi，下标 0..7 = RV1..8）并回写各釜 P。
    /// 读失败重试 1 次，仍失败抛 <see cref="GasFaultException"/>：依赖压力的序列不能盲跑。
    /// </summary>
    public async Task<double[]> ReadPressuresAsync() => (await ReadPressureSampleAsync()).Psi.ToArray();

    /// <summary>同 <see cref="ReadPressuresAsync"/>，但返回整帧（原码 + psi + 响应时刻，同一次读取），泄漏测试逐点记录用。</summary>
    public async Task<Services.PressureService.Sample> ReadPressureSampleAsync()
    {
        if (!await _press.PollAsync() && !await _press.PollAsync())
            throw new GasFaultException("压力读取失败（8AI 通讯）");
        var smp = _press.Last;
        for (int i = 0; i < 8 && i < Reactors.Count; i++)
        {
            Reactors[i].P = smp.Psi[i];
            Reactors[i].POnline = true;
        }
        return smp;
    }

    /// <summary>
    /// 管路吹扫（只吹扫总管，8 路 SV 全程关闭）：按 <see cref="PurgePlan"/> 逐帧下发并保持。
    /// progress(循环, 总循环, 阶段) 在 UI 线程回调。调用方负责 BeginGas / EndGas 与最后的全关阀。
    /// </summary>
    public async Task RunPurgeAsync(int gas, int cycles, double fillSec, double ventSec,
        Action<int, int, string>? progress, CancellationToken ct)
    {
        foreach (var a in PurgePlan.Build(gas, cycles, fillSec, ventSec))
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfGasUnsafe();
            progress?.Invoke(a.Cycle, cycles, a.Phase);
            await ApplyValvesAsync(a.Valves, ct);
            await HoldAsync(a.Valves, a.Seconds, ct);
        }
    }
}
