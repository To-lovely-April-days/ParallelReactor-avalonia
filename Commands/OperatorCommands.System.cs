using System;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Models;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Commands;

/// <summary>系统：急停 / 复位 / 全部停止 / 开始运行 / 退出。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 急停（C22）。<b>唯一允许任意线程调用的命令</b>：不断言 UI 线程，只做仲裁（Stop 类，必定允许），
    /// 然后走 <see cref="MainViewModel.RequestEstop"/> 快速路径（同步置服务层锁、直接下发停止帧，界面流程投递到 UI 线程）。
    /// 返回 Accepted、消息为空：急停面板本身就是反馈。
    /// </summary>
    public Task<OpResult> TryEstop(CommandSource src, string? note = null)
    {
        if (Arbiter.Check(ActionClass.Stop, src) is { Allowed: false } d)
            return OpResult.Fail(d.Code!, d.Message!).AsTask();
        _m.RequestEstop(string.IsNullOrWhiteSpace(note) ? src.Label : $"{src.Label} · {note}");
        return OpResult.Accepted("", "ok", new { source = src.Label }).AsTask();
    }

    /// <summary>急停复位（只允许在设备触屏上进行，附录 B）。逻辑见 <see cref="MainViewModel.ResetEstopCore"/>。</summary>
    public Task<OpResult> TryResetEstop(CommandSource src)
    {
        if (Admit(src, ActionClass.Reset, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        return _m.ResetEstopCore().AsTask();
    }

    /// <summary>
    /// 全部停止：中止任何气路占用者（C8）、中止程序，停全部通道加热并全关阀。默认不停搅拌（与今天一致）；
    /// stopStir = true 时一并停全局搅拌（远程 /stop-all 可选）。尾部等加热与阀门两条总线的结果。
    /// </summary>
    public Task<OpResult> TryStopAll(CommandSource src, bool stopStir = false)
    {
        if (Admit(src, ActionClass.Stop) is { } deny) return deny.AsTask();
        bool gas = _m.GasBusy;
        _m.AbortAllGas();   // 任何占用者的序列：收到取消后自己也会全关阀
        bool programAborted = _m.Program.AbortProgram();   // 有程序在执行也一并中止（含后台稳压回路）
        var stopped = _m.Reactors.Where(c => c.State != ReactorState.Idle).Select(c => c.Id).ToList();
        foreach (var c in _m.Reactors)
        {
            if (c.Profile.Running) c.Profile.Stop();
            c.Approach.Stop();
            if (c.State != ReactorState.Idle) c.State = ReactorState.Done;
            c.Rpm = 0;
        }
        bool stirStopped = false;
        if (stopStir && _m.StirOn) { _m.StirOn = false; stirStopped = true; }
        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        _m.UpdateRunCount();

        // 温控与阀门是两条独立总线：并行下发（停全部温控输出 逐路 At=4 + 全关 12 路阀），再按结果提示
        var heat = Services.HardwareOptions.UseRealTemp ? _m.Temp.StopAllChannelsAsync() : Task.FromResult(true);
        // report: false——失败不由 CloseAllValvesAsync 弹 Toast（远程 stop-all 不能在触屏上弹提示），由尾部记报警并经 OpResult 返回
        var valves = MainViewModel.RealIo ? _m.CloseAllValvesAsync(report: false) : Task.FromResult(true);
        var data = new { gasAborted = gas, programAborted, channels = stopped, stirStopped };
        return StopAllTailAsync(heat, valves, gas, data);
    }

    private async Task<OpResult> StopAllTailAsync(Task<bool> heat, Task<bool> valves, bool gas, object data)
    {
        bool heatOk = await heat, valvesOk = await valves;
        const string valveMsg = "阀门全关写入失败（IO16R 通讯）· 请立即手动关闭气源";
        if (!valvesOk)   // 原先由 CloseAllValvesAsync(report: true) 记的报警，文字不变
            _m.Alarm.Log("err", "系统", "阀门全关写入失败", "IO16R 通讯异常，阀门实际状态未知，请手动关闭气源");
        if (!heatOk)
        {
            _m.Alarm.Log("err", "系统", "全部停止 · 停止加热未确认", "AI-8 通讯失败，加热输出可能仍在工作");
            return OpResult.Fail(OpCodes.HardwareWriteFailed,
                "停止加热输出未确认（AI-8 通讯失败）· 请按「急停」或到设备现场确认" + (valvesOk ? "" : " · " + valveMsg), "err", data);
        }
        if (!valvesOk) return OpResult.Fail(OpCodes.HardwareWriteFailed, valveMsg, "err", data);
        return OpResult.Done(gas ? "已停止全部通道并中止气路序列 · 阀门全关" : "已停止全部通道（含加热输出）· 阀门全关",
            "warn", data);
    }

    /// <summary>
    /// 打开预运行检查框之前的预检（只读，不改状态）：仲裁 + 急停 + 气路忙。
    /// 让远程控制中的触屏在打开对话框之前就得到反馈。文字与今天「开始运行」按钮一致。
    /// </summary>
    public OpResult CheckRun(CommandSource src)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny;
        if (EstopOr("急停状态下不可启动 · 请先复位") is { } es) return es;
        if (_m.GasBusy) return OpResult.Fail(OpCodes.GasBusy, $"{_m.GasTask}进行中 · 请等待完成或先停止", "warn");
        return OpResult.Silent;
    }

    /// <summary>
    /// 开始运行（预运行检查框「确认」）。确认时按最新状态重判全部守卫（急停、气路忙 C6、预运行检查）。
    /// 只有已启用 / 已结束、且无报警和超温 / 超压未解除的通道转入「升温中」并下发加热（C19 / C20，见 <see cref="RunSelection"/>）；
    /// 需要时自动开全局搅拌。dryRun = true 只做预运行检查、不启动（对应 GET /preflight）。
    /// </summary>
    public Task<OpResult> TryRun(CommandSource src, bool dryRun = false)
    {
        if (Admit(src, dryRun ? ActionClass.Record : ActionClass.Control) is { } deny) return deny.AsTask();
        if (!dryRun)
        {
            if (EstopOr("急停状态下不可启动 · 请先复位") is { } es) return es.AsTask();
            // 气路序列（泄漏测试 / 程序气路步骤）进行中不开始运行（C6）：确认框打开期间可能刚开始了序列
            if (_m.GasBusy) return OpResult.Fail(OpCodes.GasBusy, $"{_m.GasTask}进行中 · 无法开始运行").AsTask();
        }
        _m.BuildPreRun();   // 弹窗打开期间模块可能掉线：确认时按最新状态重判
        var checks = _m.PreRunChecks.Select(x => new { kind = x.Kind, title = x.Title, detail = x.Detail }).ToList();
        var toHeat = _m.Reactors.Where(r => RunSelection.CanStartHeat(_m.RunChannelOf(r))).ToList();
        if (dryRun)
            return (_m.PreRunCanRun
                ? OpResult.Done(_m.PreRunSummary, "ok", new { canRun = true, checks, channels = toHeat.Select(c => c.Id).ToList() })
                : OpResult.Fail(OpCodes.PreflightFailed, _m.PreRunSummary, "err", new { canRun = false, checks })).AsTask();
        if (!_m.PreRunCanRun)
            return OpResult.Fail(OpCodes.PreflightFailed, "存在未处理的异常项，无法开始运行", "err", new { checks }).AsTask();

        // 开始运行即下发加热：只有已结束 / 已启用（待加热）且无超温 / 超压未解除的通道转入「升温中」并下发加热。
        // 报警（任何原因）、停用、已在运行的通道一律不动（RunSelection.CanStartHeat）。阀门不在这里动：本轮运行不使用气路
        var skipped = _m.Reactors.Where(r => r.State != ReactorState.Idle && !toHeat.Contains(r)).Select(r => r.Id).ToList();
        foreach (var c in toHeat) c.State = ReactorState.Heating;

        // 开始运行：开启全局搅拌并同步桨叶动画（搅拌未配置串口时跳过，预运行检查里已提示）
        if (!_m.StirOn && Services.HardwareOptions.UseRealStir) _m.StirOn = true;   // setter 会触发 ApplyStir
        else _m.ApplyStir();

        // 自动套档：按各釜目标温度选用对应档位的 PID，连同 SP 一起下发到温控器。
        // 定值模式走防超调逼近（先写 目标−Δ，之后由 Tick 推进）
        foreach (var c in toHeat)
        {
            if (c.SpMode == "fixed" && c.TSp >= 35) _m.StartFixedHeat(c);
            else _ = _m.StartBandHeatAsync(c.Id, c.TSp);
        }

        _m.RefreshSchematic();
        _m.Drawer.RaiseAll();
        _m.UpdateRunCount();
        return OpResult.Accepted($"预运行检查通过 · 已开始运行（{toHeat.Count} 个通道）· PID 已按温区自动套档", "ok",
            new { requested = toHeat.Select(c => c.Id).ToList(), skipped, stirOn = _m.StirOn }).AsTask();
    }

    /// <summary>
    /// 退出程序（只允许在设备触屏上进行，附录 B）：中止气路序列并等收尾、落盘设置、
    /// 停搅拌 / 停全部温控输出 / 全关阀门（三条总线并行，等写完）、写完运行记录，最后停控制循环并退出进程。
    /// </summary>
    public Task<OpResult> TryExitApp(CommandSource src)
    {
        if (Admit(src, ActionClass.Stop, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        return ExitAppTailAsync();
    }

    private async Task<OpResult> ExitAppTailAsync()
    {
        if (_m.GasBusy) await _m.AbortGasAndWaitAsync();   // 吹扫 / 泄漏测试中退出：等序列收尾、全关阀后再退出
        Services.SettingsStore.SaveNow(_m.BuildPersistState());   // 设置页的修改统一在退出时落盘

        // 退出前停搅拌、停全部温控输出、全关阀门（三条总线并行，等写完再退出，避免退出后加热器 / 电机 / 气源仍在工作）
        var stir = _m._stir.StopAsync();
        var heat = _m.Temp.StopAllChannelsAsync();
        var valves = MainViewModel.RealIo ? _m.CloseAllValvesAsync(report: false) : Task.FromResult(true);
        await Task.WhenAll(stir, heat, valves);

        // 运行记录：写完队列并 flush 再退出（最多等 3 秒，磁盘异常不阻塞退出）
        _m.Setting.FlushPendingSettingLogs();   // +/− 连点后 1.5s 内退出：修改事件不能丢
        _m.EndTestSession();
        _m.Alarm.Log("info", "系统", "程序退出",
            $"停止搅拌 {(stir.Result ? "已确认" : "未确认")} · 停止加热 {(heat.Result ? "已确认" : "未确认")} · 阀门全关 {(valves.Result ? "已确认" : "未确认")}");
        await _m.Logger.StopAsync(TimeSpan.FromSeconds(3));

        _m.ShutdownApp();   // 先停控制循环（等待期间 Tick 照常运行，与以前一致），再退出进程
        return OpResult.Silent;
    }
}
