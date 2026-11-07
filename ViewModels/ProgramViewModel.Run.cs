using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 程序执行引擎（ProgramViewModel 的执行半边）：下发后按编辑好的步骤逐条执行，
/// 釜状态随当前步骤实时变化（升温=升温中、升压=升压中、等待=等待中、搅拌=搅拌中、反应=反应中、结束降温=降温中）。
/// <para>多程序并行（api-phase3）：每通道一份 <see cref="ProgramRun"/>，最多 8 份同时执行。共用气路（总管）同一时间只由一个程序占用：
/// 气路步骤（吹扫 / 升压 / 收尾 / 稳压脉冲）经 <see cref="AcquireGasAsync"/> 按 <see cref="GasQueue"/> 先到先得排队，
/// 等待超过 AppSettings.ProgramGasWaitMin 该步骤故障。稳压改为周期短租约（脉冲间释放气路，让其他程序轮流取用）。
/// 共用搅拌器按 <see cref="StirShare"/> 引用计数：最后一个释放者才停搅拌。</para>
/// 中止入口：中止按钮 / 抽屉停止（AbortProgram(rv)，只中止该通道）、全部停止 / 急停（AbortAllPrograms），或通道报警 / 被外部置停。
/// </summary>
public partial class ProgramViewModel
{
    /// <summary>正在执行的程序（通道 → 运行对象）。ExecuteAsync 入口登记、finally 移除。</summary>
    private readonly Dictionary<int, ProgramRun> _runs = new();
    /// <summary>每通道最近一次已结束的运行：监视 / API 在程序结束后仍显示终态。</summary>
    private readonly Dictionary<int, ProgramRun> _lastRuns = new();
    /// <summary>全机最近结束的一次运行（任一通道）：API 旧字段 <c>program</c> 在没有程序执行时沿用它的终态（v0.4 语义）。</summary>
    private ProgramRun? _lastEnded;

    /// <summary>全机最近结束的一次运行信息（没有为 null）。</summary>
    public ProgramRunInfo? LastEndedRunInfo => _lastEnded?.Info;

    /// <summary>
    /// 程序方面的气路「隐性占用」：有程序在稳压（脉冲间隙气路名义空闲，但下一脉冲会整帧写阀）或有程序排队等待气路时的说明文字，
    /// 没有为 null。泄漏测试 / 手动阀控据此按 GAS_BUSY 拒绝（§7.12 第 1 条：优先级规则与 v0.4 一致）；程序之间不受影响（排队）。
    /// </summary>
    internal string? ProgramGasPending
    {
        get
        {
            foreach (var r in _runs.Values)
                if (r.IsActive && r.RegTask is { IsCompleted: false }) return $"RV{r.Rv} 程序 · 稳压";
            var q = _main.GasQueue.Waiting;
            return q.Count > 0 ? $"RV{q[0]} 程序 · 等待气路" : null;
        }
    }

    /// <summary>程序执行中的步骤故障（非气路）：程序中止并收尾。</summary>
    private sealed class ProgFaultException : Exception { public ProgFaultException(string m) : base(m) { } }

    // ============ 运行集合（命令层 / 快照构建器 / 其他 VM 读）============

    /// <summary>该通道是否被正在执行的程序占用（C20：中止、开始运行筛选、泄漏测试选通道、抽屉操作守卫一律以它为准）。</summary>
    public bool IsProgramOwned(int id) => _runs.TryGetValue(id, out var r) && r.IsActive;

    /// <summary>正在执行程序的通道号（升序）。</summary>
    public IReadOnlyList<int> RunningIds => _runs.Values.Where(r => r.IsActive).Select(r => r.Rv).OrderBy(x => x).ToList();

    public int RunningCount => _runs.Values.Count(r => r.IsActive);
    public bool AnyProgramRunning => _runs.Values.Any(r => r.IsActive);

    /// <summary>正在执行的程序信息（通道 → 只读信息），供 ProgramRunSet 小件与快照构建器使用。</summary>
    public IReadOnlyDictionary<int, ProgramRunInfo> RunInfos
        => _runs.Where(kv => kv.Value.IsActive).ToDictionary(kv => kv.Key, kv => kv.Value.Info);

    /// <summary>该通道的运行信息：正在执行的，否则最近一次已结束的（终态），都没有为 null。</summary>
    public ProgramRunInfo? RunInfoOf(int rv)
        => _runs.TryGetValue(rv, out var r) ? r.Info : _lastRuns.TryGetValue(rv, out var l) ? l.Info : null;

    /// <summary>「正在运行：RV1 RV3」（无程序时空串）。</summary>
    public string RunningSummary => ProgramRunSet.Summary(RunInfos);

    /// <summary>
    /// 首个（通道号最小）正在执行程序的通道。<b>仅供 API 顶层已废弃的 <c>program</c> 字段过渡使用</b>；
    /// 其他地方一律改用 <see cref="IsProgramOwned"/> / <see cref="RunningIds"/>。
    /// </summary>
    [Obsolete("多程序并行后请用 IsProgramOwned(id) / RunningIds；本属性只给 API 旧字段过渡")]
    public int? RunningRv => ProgramRunSet.FirstRunning(RunInfos)?.Rv;

    /// <summary>正在执行的通道中处于「搅拌中」状态的（命令层 TrySetStir 守卫用）。</summary>
    internal int? StirStepRv
    {
        get
        {
            foreach (var rv in RunningIds)
                if (_main.FindReactor(rv)?.State == ReactorState.Stir) return rv;
            return null;
        }
    }

    private ProgramRun? RunOf(int rv) => _runs.TryGetValue(rv, out var r) && r.IsActive ? r : null;

    // ============ 中止 ============

    /// <summary>
    /// 中止某通道的程序（幂等）：先同步停止曲线 / 防超调、释放搅拌引用（最后一个释放才停搅拌），并立即下发该通道的停加热，
    /// 再取消流程令牌与它自己的气路租约（只取消本通道持有的租约；正在排队的由令牌取消出队），让 ExecuteAsync 继续完成阀门和状态收尾。
    /// 其他通道的程序不受影响。
    /// </summary>
    /// <returns>是否确有程序被中止（false = 该通道没有程序在执行）。</returns>
    public bool AbortProgram(int rv)
    {
        var run = RunOf(rv);
        if (run == null) return false;

        run.Cts.Cancel();
        run.RegCts?.Cancel();
        _main.AbortGas(GasOwner.Program, rv);   // 只中止本通道程序自己的气路占用：不波及泄漏测试（C8）与其他程序

        if (_main.FindReactor(rv) is { } c)
        {
            // 这三项不等待异步流程，按下「立即停止」后立刻停止后续给定值和桨叶输出。
            c.Profile.Stop();
            c.Approach.Stop();
            ReleaseStir(run, c);

            // 停加热只发起一次；ExecuteAsync finally 会等待同一个任务，避免重复串口帧和重复提示。
            if (run.AbortOutputsTask == null)
                run.AbortOutputsTask = _main.Estopped
                    ? Task.CompletedTask
                    : _main.StopHeatAsync(c.Id, $"RV{c.Id} 程序已立即停止 · 加热输出已关闭");
        }

        if (run.CurrentIndex >= 0 && run.CurrentIndex < run.RunSteps.Count
            && run.RunSteps[run.CurrentIndex].IsCurrent)
            run.RunSteps[run.CurrentIndex].SetState(ProgramRunState.Aborted, "停止指令已下发");
        run.MonitorPhase = "正在安全停止";
        run.MonitorDetail = "正在停止加热、搅拌并完成设备收尾…";
        return true;
    }

    /// <summary>中止全部正在执行的程序（全部停止 / 急停）。返回被中止的数量。</summary>
    public int AbortAllPrograms()
    {
        int n = 0;
        foreach (var rv in RunningIds) if (AbortProgram(rv)) n++;
        return n;
    }

    /// <summary>
    /// 释放本程序的搅拌引用：仍有其他程序持有搅拌请求则保留搅拌；没有了才真正停搅拌
    /// （单程序时与原先「步骤结束 / 中止即停搅拌」一致：原先中止时搅拌开着就停，这里保持——没有其他持有者就停）。
    /// </summary>
    private void ReleaseStir(ProgramRun run, Reactor c)
    {
        run.StirHeld = false;
        int rpmBefore = _main.StirRpm;
        bool others = _main.StirShare.Release(c.Id);
        if (!others)
        {
            if (_main.StirOn && !_main.Estopped) _main.StirOn = false;
            return;
        }
        // 仍有其他程序持有：本程序若改写过全局转速，释放时恢复为剩余持有者中最近登记者的请求值并记事件
        // （否则 RV1 600 rpm 搅拌 30 min、RV2 900 rpm 搅拌 5 min 结束后，RV1 剩余时间会以 900 rpm 运行而无任何记录）
        if (_main.StirShare.Current is { } cur && cur.Rpm != rpmBefore && !_main.Estopped)
        {
            _main.Alarm.Log("warn", "系统", "搅拌转速恢复", $"搅拌转速由 RV{c.Id} 程序 {rpmBefore} rpm 恢复为 {cur.Rpm} rpm（RV{cur.Rv} 程序）");
            _main.StirRpm = cur.Rpm;
        }
    }

    // ============ 主流程 ============

    /// <summary>执行一份下发时冻结的程序副本（<see cref="StepSnapshot"/>）：执行中再编辑程序不影响本次执行。</summary>
    private async Task ExecuteAsync(ProgramRun run)
    {
        int rv = run.Rv;
        var steps = run.Steps;
        var c = _main.FindReactor(rv)!;   // 通道存在、步骤非空、该通道没有程序在执行：StartRun 已判，不满足不会建运行对象
        var ct = run.Cts.Token;
        string? fail = null;
        bool aborted = false;
        int currentIndex = -1;
        _runs[rv] = run;
        run.SetPhase(ProgramRunPhase.Running);
        ChannelOf(rv)?.SetProgramRunning(true);
        RaiseRunProjection();
        try
        {
            // 事件记录放进 try：写事件文件出错也要走 finally 清掉运行登记，否则程序永远显示执行中
            // 会话进行中（§10.3）：事件带会话 id，并写进会话 manifest 的 programRuns
            var sessionKey = _main.Logger.SessionKey;
            _main.Alarm.Log("info", "系统", $"RV{rv} 程序下发", $"共 {steps.Count} 步，开始执行 · 来源 {run.Info.StartedBy}" + (sessionKey != null ? $" · 会话 {sessionKey}" : ""));
            if (sessionKey != null) _main.Sessions.TagProgramRun(rv, DateTime.UtcNow, run.Info.StartedBy);
            for (int i = 0; i < steps.Count; i++)
            {
                // 每步开始前查取消：中止后不能再进入下一步（下一步若是升温会同步重新打开加热）
                ct.ThrowIfCancellationRequested();
                currentIndex = i;
                run.MarkStepStarted(i);
                try
                {
                    await RunStepAsync(run, c, steps[i], steps.Count, ct);
                    // 步骤可能在不可取消的等待（读压力 / 串口）期间被中止而「正常」返回：按中止处理，不标记完成
                    ct.ThrowIfCancellationRequested();
                    run.MarkStepCompleted(i);
                }
                catch (OperationCanceledException)
                {
                    run.MarkStepAborted(i);
                    throw;
                }
                catch (Exception ex)
                {
                    run.MarkStepFailed(i, ex.Message);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消来源判别：后台稳压判定的故障 → 失败；本程序令牌 / 急停 / 被外部置停 / 退出程序（AbortGasAndWaitAsync）→ 手动中止；
            // 其余只剩气路租约被超压联锁 AbortAllGas 取消（打到当前持有者）→ 持有者程序按失败收尾（其他程序的非气路步骤不受影响）
            if (run.PendingFault is { } pf) fail = pf;
            else if (ct.IsCancellationRequested || _main.Estopped || c.State is ReactorState.Done or ReactorState.Idle
                     || _main.LastGasAbortCause == GasAbortCause.Shutdown) aborted = true;
            else fail = "气路被超压联锁中止";
        }
        catch (GasFaultException ex) { fail = ex.Message; }
        catch (ProgFaultException ex) { fail = ex.Message; }
        catch (Exception ex) { fail = $"执行异常：{ex.Message}"; }
        finally
        {
            run.SetPhase(ProgramRunPhase.Finishing);
            await StopRegulationAsync(run);
            // 只收拾程序自己的气路占用（带租约代号：不能释放另一程序或泄漏测试的占用，C8）
            if (run.GasGen != 0 && _main.GasOwnedBy(GasOwner.Program, run.GasGen)) { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, run.GasGen); }
            if (c.Profile.Running) c.Profile.Stop();
            c.Approach.Stop();
            if (!_main.Estopped)
            {
                // 默认收尾：程序跑完（或中止）都停本通道加热输出并关 SV（急停时由急停流程统一处理）。
                // 中止时已发出的停加热只在之后没再碰过加热输出时复用，否则重新下发一次。
                if (run.AbortOutputsTask is { } stopTask)
                {
                    try { await stopTask; } catch { }
                    if (run.HeatTouchedAfterAbort)
                        await _main.StopHeatAsync(c.Id, $"RV{c.Id} 程序收尾 · 加热输出已停止");
                }
                else
                    await _main.StopHeatAsync(c.Id, $"RV{c.Id} 程序收尾 · 加热输出已停止");
                if (c.Valve) await _main.CloseReactorValveAsync(c.Id);
            }
            run.AbortOutputsTask = null;
            run.HeatTouchedAfterAbort = false;
            if (run.StirHeld) ReleaseStir(run, c);   // 搅拌步骤之外被中止时引用已在 AbortProgram 释放；这里兜底
            if (c.State != ReactorState.Idle) c.State = ReactorState.Done;
            c.Rpm = 0;
            run.Status = "";
            run.CurrentIndex = currentIndex;
            run.MonitorPhase = fail != null ? "程序执行失败" : aborted ? "程序已手动中止" : "程序执行完成";
            run.MonitorDetail = fail ?? (aborted ? "已停止当前程序，设备已完成安全收尾" : $"{steps.Count} 步全部完成");
            run.SetPhase(fail != null ? ProgramRunPhase.Failed : aborted ? ProgramRunPhase.Aborted : ProgramRunPhase.Completed);
            if (_runs.TryGetValue(rv, out var cur) && cur == run) _runs.Remove(rv);
            _lastRuns[rv] = run;
            _lastEnded = run;
            ChannelOf(rv)?.SetProgramRunning(false);
            RaiseRunProjection();
            _main.RefreshSchematic();
            _main.Drawer.RaiseAll();
            _main.UpdateRunCount();
        }

        if (fail != null)
        {
            _main.Toast("err", $"RV{rv} 程序已中止 · {fail}");
            _main.Alarm.Log("err", "系统", $"RV{rv} 程序中止", fail);
        }
        else if (aborted)
        {
            _main.Toast("warn", $"RV{rv} 程序已手动停止 · 加热已停，阀门全关");
            _main.Alarm.Log("warn", "系统", $"RV{rv} 程序手动停止", "加热已停，阀门全关");
        }
        else
        {
            _main.Toast("ok", $"RV{rv} 程序完成 · {steps.Count} 步全部执行");
            _main.Alarm.Log("info", "系统", $"RV{rv} 程序完成", $"{steps.Count} 步全部执行");
        }
    }

    private async Task RunStepAsync(ProgramRun run, Reactor c, StepSnapshot s, int total, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string head = $"{s.Ordinal:00}/{total:00} {s.Name}";
        // 执行前按当前全局限值再校验一次（C4：设置可能在下发后被调低）；超限拒绝本步并中止程序
        if (ProgramLimits.CheckStep(s, Limits) is { } bad)
            throw new ProgFaultException($"第 {s.Ordinal:00} 步 {s.Name} 参数超出全局限值：{bad}");
        switch (s.Type)
        {
            case StepType.Purge: await StepPurgeAsync(run, c, s, head, ct); break;
            case StepType.Heat: await StepHeatAsync(run, c, s, head, ct); break;
            case StepType.Press: await StepPressAsync(run, c, s, head, ct); break;
            case StepType.Stir: await StepStirAsync(run, c, s, head, ct); break;
            case StepType.Wait: await StepWaitAsync(run, c, s, head, ct); break;
            case StepType.React: await StepReactAsync(run, c, s, head, ct); break;
            case StepType.End: await StepEndAsync(run, c, s, head, ct); break;
        }
    }

    // ============ 气路租约（排队）============

    /// <summary>
    /// 为程序取气路租约（吹扫 / 升压 / 收尾 / 稳压脉冲共用）。规则：
    /// <list type="number">
    /// <item>气路由泄漏测试占用 → 按原规则立即故障（泄漏测试优先级不变）；</item>
    /// <item>气路空闲且无人排队 → 立即取租约（单程序时与原先完全一致，不多出任何事件）；</item>
    /// <item>否则入 <see cref="GasQueue"/>，排到队首且气路空闲才取；首次进入等待时步骤显示「等待气路（RVx 占用中）」并记事件；
    /// 等待超过 AppSettings.ProgramGasWaitMin 分钟 → 步骤故障（计时用单调时间）。排到队首后先按 GasBlock 复查：不通过
    /// （其他通道超压未解除、压力采集离线 …）则**继续等待**（文字显示原因，同一上限），而不是立即故障——联锁隔离：RV3 超压解除后 RV1 自行继续。
    /// 等待期间泄漏测试插队（理论上已被 GasGuard 按 ProgramGasPending 拒绝）按第 1 条立即故障。</item>
    /// </list>
    /// 等待期间的本通道安全检查不用 <see cref="ThrowIfStepUnsafe"/>：吹扫步骤不改釜状态，程序可以在「已结束」的通道上下发（ProgramRunGuard 允许 Done），
    /// 只有从非 Done/Idle **转入** Done/Idle（抽屉停止 / 停用 / 全部停止）才算外部停止——与 <see cref="GasLease.FaultReason"/> 同一规则。
    /// 返回本段租约代号（收尾只释放自己这段，<see cref="MainViewModel.EndGas"/>）。quiet = 稳压脉冲：不改步骤文字 / 阶段。
    /// </summary>
    private async Task<long> AcquireGasAsync(ProgramRun run, Reactor c, string task, int[] participants, int[]? watch,
        bool needPressure, bool quiet, CancellationToken ct)
    {
        if (_main.GasBusy && !_main.GasOwnedBy(GasOwner.Program))
            throw new ProgFaultException($"{_main.GasTask}进行中，请等待完成或先停止");
        if (!_main.GasBusy && _main.GasQueue.Count == 0)
        {
            _main.BeginGas(GasOwner.Program, task, participants, watch, ownerRv: c.Id);
            return run.GasGen = _main.GasGeneration;
        }

        long ticket = _main.GasQueue.Enqueue(c.Id, MainViewModel.MonoNow);
        var sw = Stopwatch.StartNew();
        var stateAtEnqueue = c.State;
        string holder = HolderText();
        string baseStatus = run.Status;
        bool announced = false;
        string? blockedWhy = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // 泄漏测试在等待期间占到了气路：按入口同一规则立即故障（泄漏测试优先级不变），不再按「等待」拖到上限
                if (_main.GasBusy && !_main.GasOwnedBy(GasOwner.Program))
                    throw new ProgFaultException($"{_main.GasTask}进行中，请等待完成或先停止");
                string? why = null;
                if (!_main.GasBusy && _main.GasQueue.IsHead(ticket)
                    && (why = _main.GasBlockReason(needPressure, selfId: c.Id, programWait: true)) == null)
                {
                    _main.GasQueue.Remove(ticket);
                    _main.BeginGas(GasOwner.Program, task, participants, watch, ownerRv: c.Id);
                    long gen = run.GasGen = _main.GasGeneration;
                    if (announced)
                    {
                        if (!quiet) run.MarkStepWaitingGas(null);
                        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 气路就位", $"等待 {sw.Elapsed.TotalSeconds:0}s（此前 {holder} 占用）· {task}");
                    }
                    return gen;
                }
                if (!announced)
                {
                    announced = true;
                    holder = HolderText();
                    run.Info.WaitingOn = _main.GasHolderRv;
                    if (!quiet) run.MarkStepWaitingGas(why ?? $"{holder} 占用中");
                    _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 等待气路", why == null
                        ? $"{task} · {holder} 占用中，排队第 {_main.GasQueue.Waiting.ToList().IndexOf(c.Id) + 1} 位"
                        : $"{task} · {why}，等待");
                    blockedWhy = why;
                }
                else if (why != null && why != blockedWhy)
                {
                    // 排到队首但公共检查不通过：记一次原因（变化时再记），继续等待
                    blockedWhy = why;
                    if (!quiet) run.MarkStepWaitingGas(why);
                    _main.Alarm.Log("warn", "系统", $"RV{c.Id} 程序 · 等待气路", $"{task} · {why}，继续等待");
                }
                double maxMin = _main.Settings.ProgramGasWaitMin;
                if (_main.GasQueue.TimedOut(ticket, MainViewModel.MonoNow, TimeSpan.FromMinutes(maxMin)))
                    throw new ProgFaultException($"等待气路超过 {maxMin:0.#} 分钟（{why ?? HolderText() + " 占用中"}）");
                if (!quiet) run.Status = $"{baseStatus} · 等待气路（{why ?? HolderText() + " 占用中"}）· 已等 {sw.Elapsed.TotalSeconds:0}s";
                await Task.Delay(TimeSpan.FromSeconds(0.5), ct);
                ThrowIfWaitUnsafe(c, stateAtEnqueue);
            }
        }
        finally
        {
            _main.GasQueue.Remove(ticket);
            run.Info.WaitingOn = null;
            if (announced && run.Info.WaitingGas) run.MarkStepWaitingGas(null);
        }
    }

    /// <summary>排队等待气路期间的本通道安全检查：急停 / 报警 / 从非 Done·Idle 转入 Done·Idle（被外部停止）。</summary>
    private void ThrowIfWaitUnsafe(Reactor c, ReactorState stateAtEnqueue)
    {
        if (_main.Estopped) throw new OperationCanceledException("急停已触发");
        if (c.State == ReactorState.Alarm) throw new ProgFaultException($"RV{c.Id} 超温 / 超压报警");
        if (c.State is ReactorState.Done or ReactorState.Idle && c.State != stateAtEnqueue)
            throw new OperationCanceledException("通道已被外部停止");
    }

    /// <summary>当前气路占用者的显示名：程序 → RVx；泄漏测试 → 任务名；空闲 → 「其他程序」。</summary>
    private string HolderText()
        => _main.GasHolderRv is { } h ? $"RV{h}" : _main.GasBusy ? _main.GasTask : "其他程序";

    // ============ 各步骤 ============

    /// <summary>吹扫：只动共用总管（SV 全关），不改釜状态；占气路（排队）。</summary>
    private async Task StepPurgeAsync(ProgramRun run, Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        await StopRegulationAsync(run);   // 吹扫需独占气路：先停后台稳压
        ct.ThrowIfCancellationRequested();   // 等稳压退出期间可能已被中止
        if (_main.GasBlockReason(needPressure: false, selfId: c.Id, programWait: true) is { } why) throw new ProgFaultException($"吹扫：{why}");
        string gas = s.Str("gas", "惰性气体");
        int cycles = (int)Math.Round(s.Num("cycles", 10));
        double fill = s.Num("fill", 3), vent = s.Num("vent", 5);
        run.Status = head;
        long gen = await AcquireGasAsync(run, c, $"RV{c.Id} 程序", new[] { c.Id }, null, needPressure: false, quiet: false, ct);
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 开始管路吹扫",
            $"{gas} × {cycles} 次 · 充 {StepDef.N(fill)}s / 排 {StepDef.N(vent)}s · SV 全关");
        var gct = _main.GasToken;
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
        try
        {
            await _main.RunPurgeAsync(PurgePlan.GasIndex(gas), cycles, fill, vent,
                (cy, n, phase) => run.Status = $"{head} · {gas} {cy}/{n} · {phase}", link.Token);
        }
        finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, gen); }
    }

    /// <summary>升温：定值（防超调逼近 + 平衡等待）或 16 段曲线（引擎私有曲线，不动通道的抽屉曲线配置）。按通道独立，无需互斥。</summary>
    private async Task StepHeatAsync(ProgramRun run, Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        if (_main.HeatBlockReason(c.Id) is { } why) throw new ProgFaultException($"升温：{why}");
        if (c.Profile.Running) c.Profile.Stop();
        c.Approach.Stop();

        if (s.Str("mode", "fixed") != "curve")
        {
            // —— 定值升温：全链启动（SP 走防超调逼近，由 Tick 推进）——
            double temp = s.Num("temp", 150), waitMin = s.Num("wait", 15);   // 35℃ 下限 / 上限已在 RunStepAsync 校验
            c.SpMode = "fixed";
            c.TSp = temp;
            _main.StartFixedHeat(c);
            SetState(c, ReactorState.Heating);
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 升温（定值）",
                $"目标 {StepDef.N(temp)}℃ · 到位后平衡 {StepDef.N(waitMin)} min");

            // 阶段 1：等实测温度到位（|PV−目标| ≤ 2℃ 才进下一步）。上限 120 分钟防加热失效空转；温控离线 60s 判失败
            var sw = Stopwatch.StartNew();
            double offlineSec = 0;
            while (!(c.TOnline && Math.Abs(c.T - temp) <= 2.0))
            {
                if (sw.Elapsed.TotalMinutes > 120)
                    throw new ProgFaultException($"升温 120 分钟未到位（当前 {Pv(c)}）· 请检查加热与温控");
                offlineSec = c.TOnline ? 0 : offlineSec + 2;
                if (offlineSec >= 60) throw new ProgFaultException("温控离线超过 60s，升温中止");
                run.Status = $"{head} · 升温 {Pv(c)} → {StepDef.N(temp)}℃"
                    + (c.Approach.StatusText.Length > 0 ? $" · {c.Approach.StatusText}" : "");
                await WaitSliceAsync(c, 2, ct);
            }

            // 阶段 2：到位后按「等待平衡」计时（0 = 不平衡，直接进下一步）
            sw.Restart();
            while (sw.Elapsed.TotalMinutes < waitMin)
            {
                run.Status = $"{head} · 已到 {StepDef.N(temp)}℃ · 平衡剩 {LeftMin(sw, waitMin)}";
                await WaitSliceAsync(c, 1, ct);
            }
            return;
        }

        // —— 曲线升温：步骤段（温度/斜率/保温）→ 引擎私有曲线，SP 由本循环周期下发 ——
        // 段来自下发快照（曲线模式缺省段已在快照里补齐），不再改动编辑中的步骤
        double startPv = c.TOnline && c.T > 1 ? c.T : 25;
        var conv = HeatCurvePlan.ToProfileSegments(s.Segments.ToList(), startPv);
        if (conv.Count == 0) throw new ProgFaultException("升温曲线为空（各段温度与起点相同且无保温段）");
        var prof = new TempProfile { SegCount = conv.Count };
        for (int i = 0; i < conv.Count; i++)
        {
            prof.Segments[i].Temp = conv[i].Temp;
            prof.Segments[i].Minutes = conv[i].Minutes;
        }
        double bandTemp = Math.Max(startPv, conv.Max(x => x.Temp));
        bool started = await _main.Temp.StartChannelAsync(c.Id, bandTemp, startPv);
        if (ct.IsCancellationRequested)
        {
            // 全链启动期间被中止：中止时的停加热可能先于这次启动落到仪表，收尾必须再停一次
            run.HeatTouchedAfterAbort = true;
            ct.ThrowIfCancellationRequested();
        }
        if (!started)
        {
            _main.ReportHeatStartFailed(c.Id);
            throw new ProgFaultException("升温曲线启动失败（温控通讯）");
        }
        prof.Start(startPv);
        SetState(c, ReactorState.Heating);
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 升温（曲线）",
            $"{conv.Count} 段 · 终温 {StepDef.N(conv[^1].Temp)}℃ · ≈ {StepDef.N(Math.Round(prof.TotalMinutes))} min");
        while (!prof.IsComplete)
        {
            double target = prof.CurrentTarget();
            c.TSp = target;   // 界面目标温度跟随曲线当前值
            if (prof.ShouldWrite(target) && await _main.WriteTempSpSafeAsync(c.Id, target))
                prof.MarkWritten(target);
            run.Status = $"{head} · 曲线 {prof.StatusText} · 当前 {Pv(c)}";
            await WaitSliceAsync(c, 2, ct);
        }
    }

    /// <summary>升压：自适应脉冲充压到目标（从下方逼近不超调）；稳压开启时启动后台稳压回路（脉冲式短租约，见 <see cref="RegulateAsync"/>）。</summary>
    private async Task StepPressAsync(ProgramRun run, Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        string gas = s.Str("gas", "气体 A");
        double target = s.Num("pres", 50);
        bool regulate = s.Str("regulate", "开启") == "开启";   // 目标上限（PMaxSp / 超压值 90%）已在 RunStepAsync 校验
        await StopRegulationAsync(run);   // 新的升压取代旧稳压
        ct.ThrowIfCancellationRequested();   // 等稳压退出期间可能已被中止：不能再把通道改回「升压中」
        // 本通道自己处于升压 / 反应（连续两个升压步骤、反应后补压）不算占用；其他程序通道的升压 / 反应也不算
        if (_main.GasBlockReason(needPressure: true, selfId: c.Id, programWait: true) is { } why) throw new ProgFaultException($"升压：{why}");

        int gi = PurgePlan.GasIndex(gas);
        c.PSp = target;
        SetState(c, ReactorState.Pressing);
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 升压", $"{gas} 到 {StepDef.N(target)} psi · 稳压{(regulate ? "开启" : "关闭")}");
        run.Status = head;
        long gen = await AcquireGasAsync(run, c, $"RV{c.Id} 程序 · 升压", new[] { c.Id }, new[] { c.Id }, needPressure: true, quiet: false, ct);
        var gct = _main.GasToken;
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
        try
        {
            await FillToAsync(run, c, gi, target, head, link.Token);
            // 最后一次读压力不可取消：期间被中止（程序中止 / 全部停止 / 联锁）时 FillToAsync 仍会正常返回，此时不能再启动稳压
            link.Token.ThrowIfCancellationRequested();
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 升压完成", $"{gas} 到 {StepDef.N(target)} psi");
        }
        finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, gen); }
        // 升压的租约已释放：稳压回路自己按脉冲排队取短租约（其他程序的气路步骤可以在脉冲间隙取用气路）
        if (regulate) StartRegulation(run, c, gi, target, ct);
    }

    /// <summary>
    /// 搅拌：全局转速下发（全机共用一台搅拌器），按「搅拌时长」停留后释放搅拌引用并进下一步。
    /// 多程序并行：登记到 <see cref="StirShare"/>；改写了另一程序设定的转速时记 warn 事件；最后一个释放者才停搅拌。
    /// </summary>
    private async Task StepStirAsync(ProgramRun run, Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        int rpm = (int)Math.Round(s.Num("rpm", 1000));
        double durMin = s.Num("dur", 5);
        SetState(c, ReactorState.Stir);
        string stopText = "搅拌停止";
        if (rpm <= 0)
        {
            // 「搅拌停止」步骤：没有其他程序持有搅拌请求时才停（单程序时与原先一致）；他人仍持有时无动作，事件如实说明
            run.StirHeld = false;
            if (_main.StirShare.Holds(c.Id)) ReleaseStir(run, c);   // 本程序此前的请求：按引用计数释放（必要时恢复他人转速）
            else if (!_main.StirShare.Release(c.Id)) _main.StirOn = false;
            if (_main.StirShare.Current is { } keep)
                stopText = $"搅拌停止请求 · RV{keep.Rv} 程序仍在搅拌，保持 {keep.Rpm} rpm";
        }
        else
        {
            var prev = _main.StirShare.Request(c.Id, rpm);
            run.StirHeld = true;
            if (prev is { } p)
                _main.Alarm.Log("warn", "系统", "搅拌转速变更", $"搅拌转速由 RV{p.Rv} 程序 {p.Rpm} rpm 改为 {rpm} rpm（RV{c.Id} 程序）");
            _main.StirRpm = rpm;
            if (!_main.StirOn) _main.StirOn = true;
        }
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 搅拌",
            rpm > 0 ? $"全局转速 {rpm} rpm（全机共用一台搅拌器）· {StepDef.N(durMin)} min" : stopText);
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalMinutes < durMin)
            {
                run.Status = $"{head} · {(rpm > 0 ? $"{rpm} rpm" : "停止")} · 剩 {LeftMin(sw, durMin)}";
                await WaitSliceAsync(c, 1, ct);
            }
        }
        finally
        {
            // 程序步骤拥有自己那份搅拌请求的生命周期：时间到或程序中止时释放；最后一个释放者才停机。
            if (rpm > 0 && run.StirHeld) ReleaseStir(run, c);
        }
    }

    /// <summary>等待：固定时长 / 等温度到位（|T−TSp|≤2℃）/ 等压力到位（|P−PSp|≤3%），均以「最长时间」为上限。</summary>
    private async Task StepWaitAsync(ProgramRun run, Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        string mode = s.Str("mode", "固定时长");
        double capMin = s.Num("time", 30);
        SetState(c, ReactorState.Wait);
        if (mode == "等待温度到位" && !c.TOnline)
        { _main.Toast("warn", $"RV{c.Id} 温控离线，「等待温度到位」按固定时长执行"); mode = "固定时长"; }
        if (mode == "等待压力到位" && !c.POnline)
        { _main.Toast("warn", $"RV{c.Id} 压力采集离线，「等待压力到位」按固定时长执行"); mode = "固定时长"; }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMinutes < capMin)
        {
            ThrowIfStepUnsafe(c);
            if (mode == "等待温度到位" && c.TOnline && Math.Abs(c.T - c.TSp) <= 2.0) break;
            if (mode == "等待压力到位" && c.POnline && Math.Abs(c.P - c.PSp) <= Math.Max(1, c.PSp * 0.03)) break;
            run.Status = $"{head} · {mode} · 剩 {LeftMin(sw, capMin)}";
            await WaitSliceAsync(c, 1, ct);
        }
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 等待完成", $"{mode} · 用时 {sw.Elapsed.TotalMinutes:0.#} min");
    }

    /// <summary>反应：倒计时。气体消耗结束条件 v1 未实现（ΔP→mmol 需釜容标定），退化为按最长时间并明确提示。</summary>
    private async Task StepReactAsync(ProgramRun run, Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        string endBy = s.Str("endBy", "两者先到");
        double timeMin = s.Num("time", 120), mmol = s.Num("mmol", 5);
        if (endBy != "时间")
        {
            _main.Toast("warn", $"RV{c.Id} 气体计量待标定（需釜容参数），「{endBy} · {StepDef.N(mmol)} mmol」按最长时间执行");
            _main.Alarm.Log("warn", "系统", $"RV{c.Id} 程序 · 反应", $"气体计量未标定，按最长时间 {StepDef.N(timeMin)} min 执行");
        }
        SetState(c, ReactorState.React);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMinutes < timeMin)
        {
            run.Status = $"{head} · 反应中 · 剩 {LeftMin(sw, timeMin)}";
            await WaitSliceAsync(c, 2, ct);
        }
    }

    /// <summary>结束：先停加热降温至 cool，再按方式收尾（密封/排空/淬灭/吹扫）。收尾的气路步骤排队取租约。</summary>
    private async Task StepEndAsync(ProgramRun run, Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        string mode = s.Str("mode", "密封 (推荐)");
        double cool = s.Num("cool", 40);
        await StopRegulationAsync(run);
        ct.ThrowIfCancellationRequested();

        // 先停加热并降温（温控离线时固定等 10 分钟兜底）
        if (c.Profile.Running) c.Profile.Stop();
        c.Approach.Stop();
        await _main.StopHeatAsync(c.Id, $"RV{c.Id} 程序 · 结束步骤停加热");
        ct.ThrowIfCancellationRequested();   // 中止后不能再把被外部停止 / 停用的通道改回「降温中」
        SetState(c, ReactorState.Cooling);
        var sw = Stopwatch.StartNew();
        double capMin = c.TOnline ? 60 : 10;
        while (sw.Elapsed.TotalMinutes < capMin)
        {
            if (c.TOnline && c.T <= cool) break;
            run.Status = $"{head} · 降温 {Pv(c)} → {StepDef.N(cool)}℃";
            await WaitSliceAsync(c, 2, ct);
        }
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 降温完成", $"当前 {Pv(c)} · 目标 {StepDef.N(cool)}℃");

        if (mode == "密封 (推荐)")
        {
            if (_main.GasBlockReason(needPressure: false, selfId: c.Id, programWait: true) is { } why) throw new ProgFaultException($"结束·密封：{why}");
            run.Status = $"{head} · 密封";
            long gen = await AcquireGasAsync(run, c, $"RV{c.Id} 程序 · 密封", new[] { c.Id }, null, needPressure: false, quiet: false, ct);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _main.GasToken);
            try { await _main.ApplyValvesAsync(ValveFrame.AllClosed(), link.Token); }
            finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, gen); }
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 密封", "SV 全关，釜内保压密闭");
        }
        else if (mode is "排空" or "淬灭")
        {
            if (_main.GasBlockReason(needPressure: true, selfId: c.Id, programWait: true) is { } why) throw new ProgFaultException($"结束·{mode}：{why}");
            if (mode == "淬灭") _main.Alarm.Log("warn", "系统", $"RV{c.Id} 程序 · 淬灭", "无淬灭硬件，按排空处理（放尽反应气）");
            run.Status = $"{head} · {mode}";
            long gen = await AcquireGasAsync(run, c, $"RV{c.Id} 程序 · {mode}", new[] { c.Id }, new[] { c.Id }, needPressure: true, quiet: false, ct);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _main.GasToken);
            try
            {
                var frame = ValveFrame.VentOnly(c.Id);
                await _main.ApplyValvesAsync(frame, link.Token);
                var vsw = Stopwatch.StartNew();
                while (vsw.Elapsed.TotalSeconds < 60)
                {
                    await _main.HoldAsync(frame, 1.0, link.Token);
                    var p = (await _main.ReadPressuresAsync())[c.Id - 1];
                    run.Status = $"{head} · {mode} · {Units.FormatP(p)}";
                    if (p <= 0.5) break;   // 回到常压（表压≈0）
                }
                await _main.ApplyValvesAsync(ValveFrame.AllClosed(), link.Token);
            }
            finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, gen); }
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · {mode}", "已排至常压并全关");
        }
        else if (mode == "吹扫")
        {
            if (_main.GasBlockReason(needPressure: false, selfId: c.Id, programWait: true) is { } why) throw new ProgFaultException($"结束·吹扫：{why}");
            run.Status = $"{head} · 吹扫";
            long gen = await AcquireGasAsync(run, c, $"RV{c.Id} 程序 · 吹扫", new[] { c.Id }, null, needPressure: false, quiet: false, ct);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _main.GasToken);
            try
            {
                await _main.RunPurgeAsync(0, 3, 3, 5,
                    (cy, n, phase) => run.Status = $"{head} · 吹扫 {cy}/{n} · {phase}", link.Token);
            }
            finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, gen); }
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 吹扫", "惰性气吹扫总管 3 循环");
        }
        SetState(c, ReactorState.Done);
    }

    // ============ 充压 / 稳压 ============

    /// <summary>单通道自适应脉冲充压：首轮 0.2s 试探，之后按实测增益（psi/开阀秒）补 60% 差值，关阀静置再读，从下方逼近。</summary>
    private async Task FillToAsync(ProgramRun run, Reactor c, int gasIdx, double target, string head, CancellationToken ct)
    {
        double tol = Math.Max(0.5, target * 0.02);
        double? gain = null;
        int stall = 0;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            _main.ThrowIfGasUnsafe();
            if (sw.Elapsed.TotalSeconds > 180) throw new GasFaultException($"RV{c.Id} 充压超时（180s）");
            var p0 = (await _main.ReadPressuresAsync())[c.Id - 1];
            if (p0 >= target - tol) return;
            double sec = gain is > 0.05 ? Math.Clamp(0.6 * (target - p0) / gain.Value, 0.1, 2.0) : 0.2;
            var frame = ValveFrame.Gas(gasIdx, c.Id);
            await _main.ApplyValvesAsync(frame, ct);
            await _main.HoldAsync(frame, sec, ct);
            await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
            await _main.HoldAsync(ValveFrame.AllClosed(), 1.0, ct);   // 静置再读，避开充气瞬间的动态压力
            var p1 = (await _main.ReadPressuresAsync())[c.Id - 1];
            gain = Math.Max(0.01, (p1 - p0) / sec);
            stall = p1 - p0 < 0.3 ? stall + 1 : 0;
            if (stall >= 5) throw new GasFaultException($"RV{c.Id} 充压无响应（气源压力不足或管路不通）");
            run.Status = $"{head} · 充压 {Units.FormatP(p1)} / {Units.FormatP(target)}";
        }
    }

    /// <summary>升压达标后启动后台稳压回路：不持有气路，低于 目标−band 时按脉冲排队取短租约补压，直到程序结束 / 被取代。</summary>
    private void StartRegulation(ProgramRun run, Reactor c, int gasIdx, double target, CancellationToken progCt)
    {
        run.RegCts?.Cancel();
        run.RegCts = new CancellationTokenSource();
        run.RegTask = RegulateAsync(run, c, gasIdx, target,
            CancellationTokenSource.CreateLinkedTokenSource(progCt, run.RegCts.Token).Token);
    }

    /// <summary>
    /// 脉冲式稳压（多程序并行）：每 2 s 读一次本通道压力，不低于 目标−band 不动气路；需要补压时先按气路公共检查复查
    /// （<see cref="MainViewModel.GasBlockReason"/>：手动阀控 / 泄漏测试 / 其他通道超压未解除 / 压力采集离线 …），通过才按队列取一段短租约
    /// （开阀 → 关阀静置 → 读压 → 释放），其他程序的气路步骤可在两次补压之间取用气路。
    /// 取舍：① 两次脉冲之间不再持续写「全关」心跳帧（与气路空闲时相同）；② 补压延迟上限 = 其他程序当前气路步骤的时长；
    /// ③ 公共检查不通过时**不取气路、继续等待**（记一次 warn，原因变化再记），连续不通过 / 排队超过 ProgramGasWaitMin 按程序故障处理
    /// （置 PendingFault 并取消流程令牌）；④ 脉冲期间的气路故障（<see cref="GasFaultException"/>：阀门 / 压力通讯、报警）同样按程序故障收尾
    /// ——v0.4 只 Toast「稳压中止」而程序继续加热、压力无人维持，v0.5 改为程序失败（见 §7.12 第 6 条）。
    /// 脉冲间隙对泄漏测试 / 手动阀控仍视为气路占用（<see cref="ProgramGasPending"/>），所以它们不会在间隙插入。本通道安全检查（报警 / 外部置停）每周期进行。
    /// </summary>
    private async Task RegulateAsync(ProgramRun run, Reactor c, int gasIdx, double target, CancellationToken ct)
    {
        double band = Math.Max(1, target * 0.03);
        double? gain = null;
        Stopwatch? blocked = null;   // 公共检查连续不通过的起点（单调计时）
        string? blockedWhy = null;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                // 本通道报警：稳压退出即可，主流程的安全检查会按原文「RVx 超温 / 超压报警」中止程序
                try { ThrowIfStepUnsafe(c); } catch (ProgFaultException) { return; }
                var p = (await _main.ReadPressuresAsync())[c.Id - 1];
                if (p >= target - band) continue;
                if (_main.GasBlockReason(needPressure: true, selfId: c.Id, programWait: true) is { } why)
                {
                    // 公共检查不通过：本周期不取气路（不能整帧写阀覆盖手动阀态 / 不能在超压未解除时补气），等待；超过上限按程序故障
                    blocked ??= Stopwatch.StartNew();
                    if (why != blockedWhy)
                    {
                        blockedWhy = why;
                        _main.Alarm.Log("warn", "系统", $"RV{c.Id} 稳压 · 等待气路",
                            $"{why} · 压力 {Units.FormatP(p)} 低于 {Units.FormatP(target - band)}，暂不补压");
                    }
                    double maxMin = _main.Settings.ProgramGasWaitMin;
                    if (maxMin > 0 && blocked.Elapsed.TotalMinutes >= maxMin)
                        throw new ProgFaultException($"等待气路超过 {maxMin:0.#} 分钟（{why}）");
                    continue;
                }
                if (blocked != null)
                {
                    _main.Alarm.Log("info", "系统", $"RV{c.Id} 稳压 · 气路恢复", $"等待 {blocked.Elapsed.TotalSeconds:0}s 后继续补压");
                    blocked = null;
                    blockedWhy = null;
                }
                long gen = await AcquireGasAsync(run, c, $"RV{c.Id} 程序 · 稳压", new[] { c.Id }, new[] { c.Id }, needPressure: true, quiet: true, ct);
                var gct = _main.GasToken;
                using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
                try
                {
                    _main.ThrowIfGasUnsafe();
                    p = (await _main.ReadPressuresAsync())[c.Id - 1];   // 排队期间压力可能变化：按取到租约时的读数补
                    if (p >= target - band) continue;
                    double sec = gain is > 0.05 ? Math.Clamp(0.6 * (target - p) / gain.Value, 0.1, 1.5) : 0.2;
                    var frame = ValveFrame.Gas(gasIdx, c.Id);
                    await _main.ApplyValvesAsync(frame, link.Token);
                    await _main.HoldAsync(frame, sec, link.Token);
                    await _main.ApplyValvesAsync(ValveFrame.AllClosed(), link.Token);
                    await _main.HoldAsync(ValveFrame.AllClosed(), 1.0, link.Token);
                    var after = (await _main.ReadPressuresAsync())[c.Id - 1];
                    gain = Math.Max(0.01, (after - p) / sec);
                    _main.Alarm.Log("info", "系统", $"RV{c.Id} 稳压补压", $"{Units.FormatP(p)} → {Units.FormatP(after)} · 目标 {Units.FormatP(target)}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && gct.IsCancellationRequested)
                {
                    // 脉冲期间租约被 AbortAllGas 取消：超压联锁 → 本程序是当前气路持有者，按程序故障收尾（其他程序不受影响）；
                    // 退出程序 → 按手动中止收尾（ExecuteAsync 读 LastGasAbortCause）
                    if (_main.LastGasAbortCause != GasAbortCause.Shutdown) run.PendingFault = "稳压：气路被超压联锁中止";
                    run.Cts.Cancel();
                    throw;
                }
                finally
                {
                    // 只有气路仍归本段租约时才收尾（被超压联锁 / 急停中止后可能已被释放，不能误关别的序列的阀）
                    if (_main.GasOwnedBy(GasOwner.Program, gen)) { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, gen); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ProgFaultException or GasFaultException)
        {
            // 等待气路超时 / 泄漏测试占用 / 脉冲期间气路故障（阀门、压力通讯、报警）：稳压是程序的一部分，
            // 压力无人维持不能让程序继续加热——按程序故障收尾（取消流程令牌，ExecuteAsync 读 PendingFault）
            run.PendingFault = $"稳压：{ex.Message}";
            _main.Alarm.Log("err", "系统", $"RV{c.Id} 稳压中止", ex.Message);
            run.Cts.Cancel();
        }
        catch (Exception ex)
        {
            _main.Toast("err", $"RV{c.Id} 稳压中止 · {ex.Message}");
            _main.Alarm.Log("err", "系统", $"RV{c.Id} 稳压中止", ex.Message);
        }
    }

    private async Task StopRegulationAsync(ProgramRun run)
    {
        run.RegCts?.Cancel();
        if (run.RegTask != null)
        {
            try { await run.RegTask; } catch { }
            run.RegTask = null;
        }
    }

    // ============ 公共小件 ============

    /// <summary>步骤执行中的安全检查：急停 / 通道报警（Tick 超温超压）/ 外部置停（全部停止、停用、抽屉停止）。只看本通道。</summary>
    private void ThrowIfStepUnsafe(Reactor c)
    {
        if (_main.Estopped) throw new OperationCanceledException("急停已触发");
        if (c.State == ReactorState.Alarm) throw new ProgFaultException($"RV{c.Id} 超温 / 超压报警");
        if (c.State is ReactorState.Done or ReactorState.Idle)
            throw new OperationCanceledException("通道已被外部停止");
    }

    /// <summary>按秒切片的等待：切片间做安全检查（取消令牌由各中止入口触发）。</summary>
    private async Task WaitSliceAsync(Reactor c, double seconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double left;
        while ((left = seconds - sw.Elapsed.TotalSeconds) > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1.0, left)), ct);
            ThrowIfStepUnsafe(c);
        }
    }

    /// <summary>切换釜状态并立即刷新气路图 / 抽屉 / 运行计数。</summary>
    private void SetState(Reactor c, ReactorState st)
    {
        c.State = st;
        _main.RefreshSchematic();
        if (_main.Drawer.IsOpen) _main.Drawer.RaiseAll();
        _main.UpdateRunCount();
    }

    private static string Pv(Reactor c) => c.TOnline ? $"{c.T:0.0}℃" : "—";
    private static string LeftMin(Stopwatch sw, double capMin) => $"{Math.Max(0, capMin - sw.Elapsed.TotalMinutes):0.#} 分";
}
