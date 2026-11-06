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
/// 气路步骤（吹扫/升压/结束收尾）按步骤独占气路；升压「稳压开启」时由后台稳压回路持气路直到程序结束。
/// 中止入口：中止按钮 / 全部停止 / 急停（AbortProgram），或通道报警 / 被外部置停。
/// </summary>
public partial class ProgramViewModel
{
    private CancellationTokenSource? _progCts;
    private CancellationTokenSource? _regCts;   // 后台稳压回路（升压步骤 regulate=开启）
    private Task? _regTask;
    private Task? _abortOutputsTask;
    /// <summary>
    /// 中止指令（含其停加热）发出之后，执行流程又碰过加热输出（例如曲线升温的全链启动恰好在中止期间完成）：
    /// finally 不能复用中止时的停加热任务，必须重新下发一次。
    /// </summary>
    private bool _heatTouchedAfterAbort;

    /// <summary>程序执行中的步骤故障（非气路）：程序中止并收尾。</summary>
    private sealed class ProgFaultException : Exception { public ProgFaultException(string m) : base(m) { } }

    /// <summary>
    /// 中止当前程序（幂等）：先同步停止曲线 / 防超调 / 搅拌，并立即下发当前通道的停加热，
    /// 再取消流程令牌，让 ExecuteAsync 继续完成阀门和状态收尾。
    /// </summary>
    /// <returns>是否确有程序被中止（false = 没有程序在执行）。</returns>
    public bool AbortProgram()
    {
        if (!IsRunningProgram) return false;

        _progCts?.Cancel();
        _regCts?.Cancel();
        _main.AbortGas(GasOwner.Program);   // 只中止程序自己的气路占用：不波及泄漏测试（C8）

        if (RunningRv is { } rv && _main.FindReactor(rv) is { } c)
        {
            // 这三项不等待异步流程，按下「立即停止」后立刻停止后续给定值和桨叶输出。
            c.Profile.Stop();
            c.Approach.Stop();
            if (_main.StirOn && !_main.Estopped) _main.StirOn = false;

            // 停加热只发起一次；ExecuteAsync finally 会等待同一个任务，避免重复串口帧和重复提示。
            if (_abortOutputsTask == null)
                _abortOutputsTask = _main.Estopped
                    ? Task.CompletedTask
                    : _main.StopHeatAsync(c.Id, $"RV{c.Id} 程序已立即停止 · 加热输出已关闭");
        }

        if (RunCurrentStepIndex >= 0 && RunCurrentStepIndex < RunSteps.Count
            && RunSteps[RunCurrentStepIndex].IsCurrent)
            RunSteps[RunCurrentStepIndex].SetState(ProgramRunState.Aborted, "停止指令已下发");
        RunMonitorPhase = "正在安全停止";
        RunMonitorDetail = "正在停止加热、搅拌并完成设备收尾…";
        return true;
    }

    // ============ 主流程 ============

    /// <summary>执行一份下发时冻结的程序副本（<see cref="StepSnapshot"/>）：执行中再编辑程序不影响本次执行。</summary>
    private async Task ExecuteAsync(int rv, IReadOnlyList<StepSnapshot> steps)
    {
        var c = _main.FindReactor(rv);
        if (c == null || steps.Count == 0) return;
        if (IsRunningProgram) return;   // 防御：同一时间只执行一个程序（入口 ConfirmRun 已守卫）
        _progCts?.Dispose();
        _progCts = new CancellationTokenSource();
        var ct = _progCts.Token;
        _heatTouchedAfterAbort = false;
        string? fail = null;
        bool aborted = false;
        int currentIndex = -1;
        RunningRv = rv;
        IsRunningProgram = true;
        try
        {
            // 事件记录放进 try：写事件文件出错也要走 finally 清掉 RunningRv / IsRunningProgram，否则程序永远显示执行中
            _main.Alarm.Log("info", "系统", $"RV{rv} 程序下发", $"共 {steps.Count} 步，开始执行");
            for (int i = 0; i < steps.Count; i++)
            {
                // 每步开始前查取消：中止后不能再进入下一步（下一步若是升温会同步重新打开加热）
                ct.ThrowIfCancellationRequested();
                currentIndex = i;
                MarkRunStepStarted(i);
                try
                {
                    await RunStepAsync(c, steps[i], steps.Count, ct);
                    // 步骤可能在不可取消的等待（读压力 / 串口）期间被中止而「正常」返回：按中止处理，不标记完成
                    ct.ThrowIfCancellationRequested();
                    MarkRunStepCompleted(i);
                }
                catch (OperationCanceledException)
                {
                    MarkRunStepAborted(i);
                    throw;
                }
                catch (Exception ex)
                {
                    MarkRunStepFailed(i, ex.Message);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) { aborted = true; }
        catch (GasFaultException ex) { fail = ex.Message; }
        catch (ProgFaultException ex) { fail = ex.Message; }
        catch (Exception ex) { fail = $"执行异常：{ex.Message}"; }
        finally
        {
            await StopRegulationAsync();
            // 只收拾程序自己的气路占用：程序在非气路步骤时可能有泄漏测试在别的通道进行，不能关它的阀、释放它的占用（C8）
            if (_main.GasOwnedBy(GasOwner.Program)) { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program); }
            if (c.Profile.Running) c.Profile.Stop();
            c.Approach.Stop();
            if (!_main.Estopped)
            {
                // 默认收尾：程序跑完（或中止）都停本通道加热输出并关 SV（急停时由急停流程统一处理）。
                // 中止时已发出的停加热只在之后没再碰过加热输出时复用，否则重新下发一次。
                if (_abortOutputsTask is { } stopTask)
                {
                    try { await stopTask; } catch { }
                    if (_heatTouchedAfterAbort)
                        await _main.StopHeatAsync(c.Id, $"RV{c.Id} 程序收尾 · 加热输出已停止");
                }
                else
                    await _main.StopHeatAsync(c.Id, $"RV{c.Id} 程序收尾 · 加热输出已停止");
                if (c.Valve) await _main.CloseReactorValveAsync(c.Id);
            }
            // 清掉本次运行的停止任务，避免下一次程序复用已经完成的任务句柄。
            _abortOutputsTask = null;
            _heatTouchedAfterAbort = false;
            if (c.State != ReactorState.Idle) c.State = ReactorState.Done;
            c.Rpm = 0;
            IsRunningProgram = false;
            RunningRv = null;
            RunStatus = "";
            RunCurrentStepIndex = currentIndex;
            RunMonitorPhase = fail != null ? "程序执行失败" : aborted ? "程序已手动中止" : "程序执行完成";
            RunMonitorDetail = fail ?? (aborted ? "已停止当前程序，设备已完成安全收尾" : $"{steps.Count} 步全部完成");
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

    private async Task RunStepAsync(Reactor c, StepSnapshot s, int total, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string head = $"{s.Ordinal:00}/{total:00} {s.Name}";
        // 执行前按当前全局限值再校验一次（C4：设置可能在下发后被调低）；超限拒绝本步并中止程序
        if (ProgramLimits.CheckStep(s, Limits) is { } bad)
            throw new ProgFaultException($"第 {s.Ordinal:00} 步 {s.Name} 参数超出全局限值：{bad}");
        switch (s.Type)
        {
            case StepType.Purge: await StepPurgeAsync(c, s, head, ct); break;
            case StepType.Heat: await StepHeatAsync(c, s, head, ct); break;
            case StepType.Press: await StepPressAsync(c, s, head, ct); break;
            case StepType.Stir: await StepStirAsync(c, s, head, ct); break;
            case StepType.Wait: await StepWaitAsync(c, s, head, ct); break;
            case StepType.React: await StepReactAsync(c, s, head, ct); break;
            case StepType.End: await StepEndAsync(c, s, head, ct); break;
        }
    }

    // ============ 各步骤 ============

    /// <summary>吹扫：只动共用总管（SV 全关），不改釜状态；占气路。</summary>
    private async Task StepPurgeAsync(Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        await StopRegulationAsync();   // 吹扫需独占气路：先停后台稳压
        ct.ThrowIfCancellationRequested();   // 等稳压退出期间可能已被中止
        if (_main.GasBlockReason(needPressure: false) is { } why) throw new ProgFaultException($"吹扫：{why}");
        string gas = s.Str("gas", "惰性气体");
        int cycles = (int)Math.Round(s.Num("cycles", 10));
        double fill = s.Num("fill", 3), vent = s.Num("vent", 5);
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 开始管路吹扫",
            $"{gas} × {cycles} 次 · 充 {StepDef.N(fill)}s / 排 {StepDef.N(vent)}s · SV 全关");
        var gct = _main.BeginGas(GasOwner.Program, $"RV{c.Id} 程序", new[] { c.Id });
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
        try
        {
            await _main.RunPurgeAsync(PurgePlan.GasIndex(gas), cycles, fill, vent,
                (cy, n, phase) => RunStatus = $"{head} · {gas} {cy}/{n} · {phase}", link.Token);
        }
        finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program); }
    }

    /// <summary>升温：定值（防超调逼近 + 平衡等待）或 16 段曲线（引擎私有曲线，不动通道的抽屉曲线配置）。</summary>
    private async Task StepHeatAsync(Reactor c, StepSnapshot s, string head, CancellationToken ct)
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
                RunStatus = $"{head} · 升温 {Pv(c)} → {StepDef.N(temp)}℃"
                    + (c.Approach.StatusText.Length > 0 ? $" · {c.Approach.StatusText}" : "");
                await WaitSliceAsync(c, 2, ct);
            }

            // 阶段 2：到位后按「等待平衡」计时（0 = 不平衡，直接进下一步）
            sw.Restart();
            while (sw.Elapsed.TotalMinutes < waitMin)
            {
                RunStatus = $"{head} · 已到 {StepDef.N(temp)}℃ · 平衡剩 {LeftMin(sw, waitMin)}";
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
            _heatTouchedAfterAbort = true;
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
            RunStatus = $"{head} · 曲线 {prof.StatusText} · 当前 {Pv(c)}";
            await WaitSliceAsync(c, 2, ct);
        }
    }

    /// <summary>升压：自适应脉冲充压到目标（从下方逼近不超调）；稳压开启时移交后台稳压回路。</summary>
    private async Task StepPressAsync(Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        string gas = s.Str("gas", "气体 A");
        double target = s.Num("pres", 50);
        bool regulate = s.Str("regulate", "开启") == "开启";   // 目标上限（PMaxSp / 超压值 90%）已在 RunStepAsync 校验
        await StopRegulationAsync();   // 新的升压取代旧稳压
        ct.ThrowIfCancellationRequested();   // 等稳压退出期间可能已被中止：不能再把通道改回「升压中」
        // 本通道自己处于升压 / 反应（连续两个升压步骤、反应后补压）不算占用
        if (_main.GasBlockReason(needPressure: true, selfId: c.Id) is { } why) throw new ProgFaultException($"升压：{why}");

        int gi = PurgePlan.GasIndex(gas);
        c.PSp = target;
        SetState(c, ReactorState.Pressing);
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 升压", $"{gas} 到 {StepDef.N(target)} psi · 稳压{(regulate ? "开启" : "关闭")}");
        var gct = _main.BeginGas(GasOwner.Program, $"RV{c.Id} 程序 · 升压", new[] { c.Id }, new[] { c.Id });
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
        bool handOver = false;
        try
        {
            await FillToAsync(c, gi, target, head, link.Token);
            // 最后一次读压力不可取消：期间被中止（程序中止 / 全部停止 / 联锁）时 FillToAsync 仍会正常返回，
            // 此时不能再移交稳压（续租会换出未取消的新令牌）
            link.Token.ThrowIfCancellationRequested();
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 升压完成", $"{gas} 到 {StepDef.N(target)} psi");
            if (regulate) { StartRegulation(c, gi, target, ct); handOver = true; }
        }
        finally { if (!handOver) { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program); } }
    }

    /// <summary>搅拌：全局转速下发（全机共用一台搅拌器），按「搅拌时长」停留后自动停搅拌并进下一步。</summary>
    private async Task StepStirAsync(Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        int rpm = (int)Math.Round(s.Num("rpm", 1000));
        double durMin = s.Num("dur", 5);
        SetState(c, ReactorState.Stir);
        if (rpm <= 0) _main.StirOn = false;
        else { _main.StirRpm = rpm; if (!_main.StirOn) _main.StirOn = true; }
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 搅拌",
            rpm > 0 ? $"全局转速 {rpm} rpm（全机共用一台搅拌器）· {StepDef.N(durMin)} min" : "搅拌停止");
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalMinutes < durMin)
            {
                RunStatus = $"{head} · {(rpm > 0 ? $"{rpm} rpm" : "停止")} · 剩 {LeftMin(sw, durMin)}";
                await WaitSliceAsync(c, 1, ct);
            }
        }
        finally
        {
            // 程序步骤拥有搅拌输出的生命周期：时间到或程序中止时都要明确停机。
            if (rpm > 0 && _main.StirOn) _main.StirOn = false;
        }
    }

    /// <summary>等待：固定时长 / 等温度到位（|T−TSp|≤2℃）/ 等压力到位（|P−PSp|≤3%），均以「最长时间」为上限。</summary>
    private async Task StepWaitAsync(Reactor c, StepSnapshot s, string head, CancellationToken ct)
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
            RunStatus = $"{head} · {mode} · 剩 {LeftMin(sw, capMin)}";
            await WaitSliceAsync(c, 1, ct);
        }
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 等待完成", $"{mode} · 用时 {sw.Elapsed.TotalMinutes:0.#} min");
    }

    /// <summary>反应：倒计时。气体消耗结束条件 v1 未实现（ΔP→mmol 需釜容标定），退化为按最长时间并明确提示。</summary>
    private async Task StepReactAsync(Reactor c, StepSnapshot s, string head, CancellationToken ct)
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
            RunStatus = $"{head} · 反应中 · 剩 {LeftMin(sw, timeMin)}";
            await WaitSliceAsync(c, 2, ct);
        }
    }

    /// <summary>结束：先停加热降温至 cool，再按方式收尾（密封/排空/淬灭/吹扫）。</summary>
    private async Task StepEndAsync(Reactor c, StepSnapshot s, string head, CancellationToken ct)
    {
        string mode = s.Str("mode", "密封 (推荐)");
        double cool = s.Num("cool", 40);
        await StopRegulationAsync();
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
            RunStatus = $"{head} · 降温 {Pv(c)} → {StepDef.N(cool)}℃";
            await WaitSliceAsync(c, 2, ct);
        }
        _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 降温完成", $"当前 {Pv(c)} · 目标 {StepDef.N(cool)}℃");

        if (mode == "密封 (推荐)")
        {
            if (_main.GasBlockReason(needPressure: false) is { } why) throw new ProgFaultException($"结束·密封：{why}");
            var gct = _main.BeginGas(GasOwner.Program, $"RV{c.Id} 程序 · 密封", new[] { c.Id });
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
            try { await _main.ApplyValvesAsync(ValveFrame.AllClosed(), link.Token); }
            finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program); }
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 密封", "SV 全关，釜内保压密闭");
        }
        else if (mode is "排空" or "淬灭")
        {
            if (_main.GasBlockReason(needPressure: true) is { } why) throw new ProgFaultException($"结束·{mode}：{why}");
            if (mode == "淬灭") _main.Alarm.Log("warn", "系统", $"RV{c.Id} 程序 · 淬灭", "无淬灭硬件，按排空处理（放尽反应气）");
            var gct = _main.BeginGas(GasOwner.Program, $"RV{c.Id} 程序 · {mode}", new[] { c.Id }, new[] { c.Id });
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
            try
            {
                var frame = ValveFrame.VentOnly(c.Id);
                await _main.ApplyValvesAsync(frame, link.Token);
                var vsw = Stopwatch.StartNew();
                while (vsw.Elapsed.TotalSeconds < 60)
                {
                    await _main.HoldAsync(frame, 1.0, link.Token);
                    var p = (await _main.ReadPressuresAsync())[c.Id - 1];
                    RunStatus = $"{head} · {mode} · {Units.FormatP(p)}";
                    if (p <= 0.5) break;   // 回到常压（表压≈0）
                }
                await _main.ApplyValvesAsync(ValveFrame.AllClosed(), link.Token);
            }
            finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program); }
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · {mode}", "已排至常压并全关");
        }
        else if (mode == "吹扫")
        {
            if (_main.GasBlockReason(needPressure: false) is { } why) throw new ProgFaultException($"结束·吹扫：{why}");
            var gct = _main.BeginGas(GasOwner.Program, $"RV{c.Id} 程序 · 吹扫", new[] { c.Id });
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, gct);
            try
            {
                await _main.RunPurgeAsync(0, 3, 3, 5,
                    (cy, n, phase) => RunStatus = $"{head} · 吹扫 {cy}/{n} · {phase}", link.Token);
            }
            finally { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program); }
            _main.Alarm.Log("info", "系统", $"RV{c.Id} 程序 · 吹扫", "惰性气吹扫总管 3 循环");
        }
        SetState(c, ReactorState.Done);
    }

    // ============ 充压 / 稳压 ============

    /// <summary>单通道自适应脉冲充压：首轮 0.2s 试探，之后按实测增益（psi/开阀秒）补 60% 差值，关阀静置再读，从下方逼近。</summary>
    private async Task FillToAsync(Reactor c, int gasIdx, double target, string head, CancellationToken ct)
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
            RunStatus = $"{head} · 充压 {Units.FormatP(p1)} / {Units.FormatP(target)}";
        }
    }

    /// <summary>升压达标后启动后台稳压回路：接管气路，低于 目标−band 时按增益脉冲补压，直到程序结束/被取代。</summary>
    private void StartRegulation(Reactor c, int gasIdx, double target, CancellationToken progCt)
    {
        _regCts?.Cancel();
        _regCts = new CancellationTokenSource();
        var gct = _main.BeginGas(GasOwner.Program, $"RV{c.Id} 程序 · 稳压", new[] { c.Id }, new[] { c.Id });
        long gen = _main.GasGeneration;   // 收尾只释放本段租约：晚到的 finally 不能释放之后的程序气路占用
        _regTask = RegulateAsync(c, gasIdx, target, gen,
            CancellationTokenSource.CreateLinkedTokenSource(progCt, gct, _regCts.Token).Token);
    }

    private async Task RegulateAsync(Reactor c, int gasIdx, double target, long gen, CancellationToken ct)
    {
        double band = Math.Max(1, target * 0.03);
        double? gain = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await _main.HoldAsync(ValveFrame.AllClosed(), 2.0, ct);   // 等待 + 帧心跳（内含超压/急停检查）
                var p = (await _main.ReadPressuresAsync())[c.Id - 1];
                if (p >= target - band) continue;
                double sec = gain is > 0.05 ? Math.Clamp(0.6 * (target - p) / gain.Value, 0.1, 1.5) : 0.2;
                var frame = ValveFrame.Gas(gasIdx, c.Id);
                await _main.ApplyValvesAsync(frame, ct);
                await _main.HoldAsync(frame, sec, ct);
                await _main.ApplyValvesAsync(ValveFrame.AllClosed(), ct);
                await _main.HoldAsync(ValveFrame.AllClosed(), 1.0, ct);
                var after = (await _main.ReadPressuresAsync())[c.Id - 1];
                gain = Math.Max(0.01, (after - p) / sec);
                _main.Alarm.Log("info", "系统", $"RV{c.Id} 稳压补压", $"{Units.FormatP(p)} → {Units.FormatP(after)} · 目标 {Units.FormatP(target)}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _main.Toast("err", $"RV{c.Id} 稳压中止 · {ex.Message}");
            _main.Alarm.Log("err", "系统", $"RV{c.Id} 稳压中止", ex.Message);
        }
        finally
        {
            // 只有气路仍归程序时才收尾（被超压联锁 / 急停中止后可能已被释放，不能误关别的序列的阀）
            if (_main.GasOwnedBy(GasOwner.Program, gen)) { await _main.CloseAllValvesAsync(); _main.EndGas(GasOwner.Program, gen); }
        }
    }

    private async Task StopRegulationAsync()
    {
        _regCts?.Cancel();
        if (_regTask != null)
        {
            try { await _regTask; } catch { }
            _regTask = null;
        }
    }

    // ============ 公共小件 ============

    /// <summary>步骤执行中的安全检查：急停 / 通道报警（Tick 超温超压）/ 外部置停（全部停止、停用、抽屉停止）。</summary>
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
