using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 气路自动序列（程序「吹扫」步骤 / 主界面泄漏测试）的公共底层：占用与中止、整帧写阀并同步界面、
/// 即时读压力、保持阀态（带 IO16R 心跳）。同一时间只允许一个序列占用气路。
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

    /// <summary>手动阀操作的守卫：急停锁定中、或自动序列进行中（阀门由序列控制），拒绝手动操作。</summary>
    private bool GasLockedForManual()
    {
        if (Estopped)
        {
            Toast("err", "急停状态下禁止阀门操作 · 请先复位");
            return true;
        }
        if (!GasBusy) return false;
        Toast("warn", $"{GasTask}进行中 · 阀门由自动序列控制");
        return true;
    }

    private CancellationTokenSource? _gasCts;

    /// <summary>
    /// 开始自动序列前的公共检查：返回 null 表示可以开始，否则为拒绝原因。
    /// needPressure：序列是否依赖压力读数（泄漏测试要闭环充压与超压监视；吹扫只动总管、SV 全关，不依赖）。
    /// </summary>
    public string? GasBlockReason(bool needPressure = true)
    {
        if (EstopLocked) return "急停状态下禁止气路操作";
        if (!RealIo) return "阀门模块未配置串口（PR_IO_PORT），无法执行气路操作";
        if (needPressure && !Services.HardwareOptions.UseRealAnalog)
            return "压力采集未配置串口（PR_PRESS_PORT），无法闭环充压与超压监视";
        if (needPressure && !_press.Online) return "压力采集离线（8AI 通讯失败），请检查接线与串口后再试";
        if (GasBusy) return $"{GasTask}进行中，请等待完成或先停止";
        if (ManualValve) return "请先关闭「手动阀控」：自动序列与手动阀操作不能同时进行";
        var op = Reactors.Where(r => IsOverPressureTripped(r.Id)).ToList();
        if (op.Count > 0)
            return $"{string.Join("、", op.Select(r => "RV" + r.Id))} 超压未解除（需低于超压报警值的 98%），禁止气路操作";
        // 停用 / 已结束通道的超温切断报警不阻塞气路（见 IsIdleOverTempAlarm）
        var busy = Reactors.Where(r => (r.State is ReactorState.Pressing or ReactorState.React or ReactorState.Alarm)
                                       && !IsIdleOverTempAlarm(r)).ToList();
        if (busy.Count > 0)
            return $"{string.Join("、", busy.Select(r => "RV" + r.Id))} 正在加压 / 反应 / 报警，需先停止";
        return null;
    }

    private int[] _gasWatch = Array.Empty<int>();   // 序列期间做超压监视的 RV（会开 SV 充压的通道）

    /// <summary>
    /// 占用气路，返回本次序列的取消令牌（全部停止 / 急停 / 中止按钮会触发取消）。
    /// watchIds：序列期间按压力做超压监视的 RV（泄漏测试的被测通道；吹扫不开 SV，为空）。
    /// </summary>
    public CancellationToken BeginGas(string task, int[]? watchIds = null)
    {
        _gasCts?.Dispose();
        _gasCts = new CancellationTokenSource();
        _gasWatch = watchIds ?? Array.Empty<int>();
        GasTask = task;
        return _gasCts.Token;
    }

    /// <summary>释放气路（序列的 finally 里、全关阀之后调用）。</summary>
    public void EndGas()
    {
        _abortKeepVent = false;
        _gasCts?.Dispose();
        _gasCts = null;
        _gasWatch = Array.Empty<int>();
        GasTask = "";
    }

    /// <summary>请求中止当前序列（序列自己的 finally 负责全关阀）。</summary>
    public void AbortGas() => _gasCts?.Cancel();

    /// <summary>
    /// 中止并等序列收尾（其 finally 全关阀后才释放气路），最多等 timeoutMs，再兜底全关一次。退出程序前调用：
    /// 序列可能还有一帧开阀写在串口队列里，只发全关就退出的话，最后落到 IO16R 的可能是那帧开阀。
    /// </summary>
    public async Task AbortGasAndWaitAsync(int timeoutMs = 3000)
    {
        AbortGas();
        var sw = Stopwatch.StartNew();
        while (GasBusy && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        await CloseAllValvesAsync();
    }

    /// <summary>
    /// 序列运行中的安全检查：急停 / 任一釜报警 / 被监视通道超压时抛出，序列中止并全关阀。
    /// 被监视通道按压力直接判超压（Tick 只给运行中的釜置报警态，泄漏测试的釜多为空闲态）。
    /// </summary>
    public void ThrowIfGasUnsafe()
    {
        if (Estopped) throw new GasFaultException("急停已触发");
        // 停用 / 已结束通道的超温报警只在该通道参与本序列（充压监视）时才中止
        var a = Reactors.FirstOrDefault(r => r.State == ReactorState.Alarm && !(IsIdleOverTempAlarm(r) && !_gasWatch.Contains(r.Id)));
        if (a != null) throw new GasFaultException($"RV{a.Id} 报警");
        var o = Reactors.FirstOrDefault(r => _gasWatch.Contains(r.Id) && r.P >= Settings.OverPressure);
        if (o != null) throw new GasFaultException($"RV{o.Id} 超压（{Units.FormatP(o.P)}）");
    }

    /// <summary>
    /// 整帧写 12 路阀门（见 <see cref="ValveFrame"/>）并同步界面。不安全的阀态直接拒绝；
    /// 真机写失败重试 2 次，仍失败抛 <see cref="GasFaultException"/>。
    /// </summary>
    public async Task ApplyValvesAsync(bool[] frame, CancellationToken ct = default)
    {
        if (!ValveFrame.IsSafe(frame))
            throw new GasFaultException("阀态不安全（多路气源同开，或气源与排空同开），已拒绝下发");
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
