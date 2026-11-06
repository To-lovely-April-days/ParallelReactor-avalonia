using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Models;
using ParallelReactor.Services;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 数据记录与安全事件：每 Tick 一行运行记录、模块掉线 / 恢复、超温切断、超压事件、测试会话与标记、阀门整帧变化。
/// 写盘全部在 <see cref="DataLogger"/> 的后台任务里，UI 线程只做格式化与入队。
/// </summary>
public partial class MainViewModel
{
    /// <summary>运行记录 / 事件日志（数据目录：PR_DATA_DIR，默认 ~/ParallelReactor-data）。</summary>
    public DataLogger Logger { get; }

    /// <summary>写盘失败（后台线程回调）：切回 UI 线程提示一次并记事件，程序照常运行。</summary>
    private void OnLoggerError(string msg) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Toast("err", $"运行记录写盘失败 · {msg} · 程序继续运行");
        Alarm?.Log("err", "系统", "运行记录写盘失败", msg);
    });

    /// <summary>只写事件日志文件、不进界面报警列表（高频的自动动作：阀门整帧、急停各部分回应时刻）。</summary>
    private void LogFileEvent(string kind, string source, string title, string detail)
        => Logger.Event(DateTimeOffset.Now, kind, source, title, detail);

    // ============ 测试会话 / 标记（供测试流程调用；暂无界面入口）============

    /// <summary>开始测试会话：之后的运行记录另写一份到 sessions/&lt;testId&gt;-&lt;时间&gt;.csv。返回会话文件路径。</summary>
    public string BeginTestSession(string testId)
    {
        EndTestSession();
        var path = Logger.BeginSession(testId);
        Logger.AddMarker($"会话开始 {Logger.SessionId}");
        Alarm.Log("info", "系统", "测试会话开始", $"{Logger.SessionId} · {path}");
        return path;
    }

    /// <summary>结束当前测试会话（无会话时不做任何事）。</summary>
    public void EndTestSession()
    {
        var id = Logger.SessionId;
        if (id == null) return;
        string end = $"会话结束 {id}";
        Logger.AddMarker(end);   // 日运行记录：下一行
        Logger.EndSession(BuildTelemetryRow(Temp.Online, _press.Online), end);   // 会话文件：立即补写带结束标记的最后一行
        Alarm.Log("info", "系统", "测试会话结束", id);
    }

    /// <summary>添加标记：出现在下一行运行记录的「标记」列，同时记一条事件。</summary>
    public void AddTestMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Logger.AddMarker(text);
        Alarm.Log("info", "系统", "标记", text);
    }

    // ============ 设置修改事件 ============

    /// <summary>记一条设置修改事件（键、旧值、新值）。</summary>
    public void LogSetting(string key, string oldValue, string newValue)
    {
        if (oldValue == newValue) return;
        Alarm.Log("info", "设置", $"修改 · {key}", $"{oldValue} → {newValue}");
    }

    // ============ 超温切断（P0-4）============

    private readonly OverTempGuard _otGuard = new(8);
    private readonly DateTime[] _otFailLogAt = new DateTime[8];

    /// <summary>该通道（1 起）超温切断已触发且未解除（启动加热 / 自整定前检查用）。</summary>
    public bool IsOverTempTripped(int id) => id >= 1 && id <= _otGuard.Count && _otGuard.IsTripped(id - 1);

    /// <summary>
    /// 每 Tick 对每个通道调用（不论状态：停用 / 已结束 / 自整定中同样判定）。
    /// 上升沿：下发 At=4（含取消自整定）+ 置报警 + 记事件；未确认（回读 At≠4）时每 5 秒重发；温控离线时不判（PV 是旧值）。
    /// 触发未解除期间通道被复位出报警态（停止 / 停用 / 急停）时重新置报警，保证仍超温的通道始终显示「超温」。
    /// </summary>
    private void CheckOverTemp(Reactor c, bool realT)
    {
        int i = c.Id - 1;
        var ch = Temp.Channels[i];
        double limit = Settings.OverTemp;
        bool? stopped = ch.Mode < 0 ? null : ch.Mode == 4;
        var now = DateTime.UtcNow;
        var act = _otGuard.Evaluate(i, ch.Pv, realT && ch.Online, limit, now, stopped);
        // 先下发（在任何 UI / 日志代码之前，后者即使抛异常也不会让切断漏发）
        if (act is OverTempAction.TripSend or OverTempAction.RetrySend or OverTempAction.Reopened)
            _ = SendOverTempStopAsync(c.Id, act);
        switch (act)
        {
            case OverTempAction.TripSend:
                string was = c.StateZh;
                bool tuning = ch.Autotuning;
                SetOverTempAlarm(c);
                Alarm.Log("err", $"RV{c.Id}", "超温 · 切断加热",
                    $"PV {Fx(ch.Pv, "0.0")}℃ ≥ 超温限值 {Fx(limit, "0.#")}℃ · 原状态 {was}" + (tuning ? " · 自整定中（已取消）" : "") + " · 下发 At=4");
                Toast("err", $"RV{c.Id} 超温（{Fx(ch.Pv, "0.0")}℃）· 已切断加热输出");
                return;
            case OverTempAction.Reopened:
                SetOverTempAlarm(c);
                Alarm.Log("err", $"RV{c.Id}", "超温切断后输出被重新打开 · 重新切断",
                    $"PV {Fx(ch.Pv, "0.0")}℃ · 回读 At={ch.Mode}（≠4）· 重发 At=4");
                Toast("err", $"RV{c.Id} 仍超温且加热输出被重新打开 · 已重新切断");
                return;
            case OverTempAction.Confirmed:
                Alarm.Log("info", $"RV{c.Id}", "超温切断已确认", $"回读 At=4，加热输出已停止 · PV {Fx(ch.Pv, "0.0")}℃");
                break;
            case OverTempAction.Cleared:
                Alarm.Log("info", $"RV{c.Id}", "超温解除",
                    $"PV {Fx(ch.Pv, "0.0")}℃ < {Fx(limit - OverTempGuard.Hysteresis, "0.#")}℃（限值 − 回差 {Fx(OverTempGuard.Hysteresis, "0.#")}℃）· 通道状态需手动复位");
                return;
        }
        // 触发未解除、但通道已被复位出报警态（停止 / 停用 / 急停把状态改成了停用 / 已结束）：重新置报警
        if (_otGuard.IsTripped(i) && c.State != ReactorState.Alarm)
        {
            string was = c.StateZh;
            SetOverTempAlarm(c);
            Alarm.Log("warn", $"RV{c.Id}", "仍超温 · 重新置报警",
                $"PV {Fx(ch.Pv, "0.0")}℃（解除需低于 {Fx(limit - OverTempGuard.Hysteresis, "0.#")}℃）· 原状态 {was}");
        }
    }

    /// <summary>超温报警：记下报警前是否为停用 / 已结束（这类通道的超温报警不阻塞其他通道的气路操作）。</summary>
    private void SetOverTempAlarm(Reactor c)
    {
        int i = c.Id - 1;
        if (c.State != ReactorState.Alarm)
            _otIdleAlarm[i] = c.State is ReactorState.Idle or ReactorState.Done;
        c.State = ReactorState.Alarm;
        c.OverTemp = true;
    }

    /// <summary>超温报警发生时通道处于停用 / 已结束（未在运行）。</summary>
    private readonly bool[] _otIdleAlarm = new bool[8];

    /// <summary>
    /// 该报警是否只是「停用 / 已结束通道的超温切断」：这类报警不阻塞、也不中止其他通道的气路序列
    /// （未接热电偶的空通道开路时可能读数偏高，否则会让 RV1-4 的泄漏测试 / 吹扫全部无法进行）。
    /// 运行中通道的报警、超压报警照旧阻塞。
    /// </summary>
    private bool IsIdleOverTempAlarm(Reactor r)
        => r.State == ReactorState.Alarm && r.OverTemp && _otIdleAlarm[r.Id - 1] && _otGuard.IsTripped(r.Id - 1);

    private async Task SendOverTempStopAsync(int id, OverTempAction why)
    {
        bool ok;
        var t0 = DateTimeOffset.Now;
        try { ok = await Temp.OverTempStopAsync(id); }
        catch { ok = false; }
        _otGuard.OnStopResult(id - 1, ok, DateTime.UtcNow);
        int sends = _otGuard.UnconfirmedSends(id - 1);
        string what = why == OverTempAction.RetrySend ? $"重发 At=4（第 {sends} 次）" : "下发 At=4";
        LogFileEvent(ok ? "info" : "err", $"RV{id}", ok ? "超温切断 · 写入回显正常" : "超温切断 · 写入失败",
            $"{what} · 下发 {Csv.Time(t0)}" + (ok ? " · 等待回读 At=4 确认" : " · AI-8 通讯失败"));
        if (!ok && (why != OverTempAction.RetrySend || DateTime.UtcNow - _otFailLogAt[id - 1] >= TimeSpan.FromMinutes(1)))
        {
            _otFailLogAt[id - 1] = DateTime.UtcNow;
            Alarm.Log("err", $"RV{id}", "超温切断未确认", $"{what}失败（AI-8 通讯）· 超温期间约每 5 秒重发 · 必要时使用急停");
        }
        if (why == OverTempAction.RetrySend && sends == OverTempGuard.EscalateAfterSends && _otGuard.IsTripped(id - 1) && !_otGuard.IsConfirmed(id - 1))
        {
            Alarm.Log("err", $"RV{id}", "超温切断多次未确认 · 请使用急停",
                $"已下发 {sends} 次 At=4 仍未回读到停止（At={Temp.Channels[id - 1].Mode}）· 加热输出可能仍在工作，请按「急停」或到现场断电");
            Toast("err", $"RV{id} 超温切断多次未确认 · 请按「急停」或到现场断电");
        }
    }

    /// <summary>事件文字里的数值一律按 InvariantCulture（小数点「.」），与 CSV 数据列一致。</summary>
    private static string Fx(double v, string fmt) => v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);

    // ============ 超压事件（只记事件，报警行为不变）============

    private readonly bool[] _overP = new bool[8];

    private void CheckOverPressure(Reactor c, bool realP)
    {
        if (!realP) return;
        int i = c.Id - 1;
        double lim = Settings.OverPressure;
        if (!_overP[i] && c.P >= lim)
        {
            _overP[i] = true;
            bool sets = c.State is not (ReactorState.Idle or ReactorState.Done);
            Alarm.Log("err", $"RV{c.Id}", "超压",
                $"压力 {Units.FormatP(c.P)} ≥ 超压报警值 {Units.FormatP(lim)} · 状态 {c.StateZh}" + (sets ? "（置报警）" : "（停用 / 已结束，不改状态）"));
        }
        else if (_overP[i] && c.P < lim * 0.98)
        {
            _overP[i] = false;
            Alarm.Log("info", $"RV{c.Id}", "超压解除", $"压力 {Units.FormatP(c.P)} < 超压报警值的 98%");
        }
    }

    // ============ 模块掉线 / 恢复（只记跳变）============

    private int _modTicks;
    private readonly bool?[] _modPrev = new bool?[4];

    private void TrackModules()
    {
        if (++_modTicks < 5) return;   // 开机头几个 Tick 首轮通讯还没回来，不判
        TrackModule(0, "温控 AI-8", HardwareOptions.UseRealTemp, Temp.Online, Temp.Stats);
        TrackModule(1, "压力 8AI", HardwareOptions.UseRealAnalog, _press.Online, _press.Stats);
        TrackModule(2, "阀门 IO16R", RealIo, _valves.Online, _valves.Stats);
        TrackModule(3, "搅拌 DM2C", HardwareOptions.UseRealStir, _stir.Online, _stir.Stats);
    }

    private void TrackModule(int k, string name, bool configured, bool online, CommStats s)
    {
        if (!configured) return;   // 未配置串口的模块恒为离线，不记
        var prev = _modPrev[k];
        _modPrev[k] = online;
        string stat = $"累计事务 {s.Polls} · 失败 {s.Fails} · 最近响应 {(s.LastOkAt is { } t ? Csv.Time(t) : "无")}";
        if (prev == null)
        {
            if (!online) Alarm.Log("err", "通讯", $"{name} 离线", "开机后未连通 · " + stat);
            return;
        }
        if (prev == online) return;
        if (online) Alarm.Log("info", "通讯", $"{name} 通讯恢复", stat);
        else Alarm.Log("err", "通讯", $"{name} 离线", "连续 3 次通讯失败 · " + stat);
    }

    // ============ 阀门整帧变化（只写事件文件）============

    private static string ValveName(int i) => i switch
    {
        < 8 => $"SV{i + 1}",
        ValveFrame.GasBase => "惰性气体",
        ValveFrame.GasBase + 1 => "气体A",
        ValveFrame.GasBase + 2 => "气体B",
        _ => "排空",
    };

    /// <summary>当前界面阀态（已确认下发的状态）的 12 路帧。</summary>
    private bool[] ShownValveFrame()
    {
        var f = ValveFrame.AllClosed();
        for (int i = 0; i < 8 && i < Reactors.Count; i++) f[i] = Reactors[i].Valve;
        for (int g = 0; g < 3 && g < GasInlets.Count; g++) f[ValveFrame.GasBase + g] = GasInlets[g].On;
        f[ValveFrame.Vent] = VentOn;
        return f;
    }

    /// <summary>整帧阀态确认写入后调用：与上一帧比，有变化才记一条（序列心跳重写不记）。</summary>
    private void LogValveFrame(bool[] next)
    {
        var prev = ShownValveFrame();
        var diff = new List<string>();
        for (int i = 0; i < ValveFrame.Count && i < next.Length; i++)
            if (prev[i] != next[i]) diff.Add($"{ValveName(i)}{(next[i] ? "开" : "关")}");
        if (diff.Count == 0) return;
        var open = Enumerable.Range(0, Math.Min(ValveFrame.Count, next.Length)).Where(i => next[i]).Select(ValveName).ToList();
        LogFileEvent("info", "阀门", "阀门整帧下发",
            $"{string.Join(" ", diff)} · 当前开启：{(open.Count == 0 ? "无" : string.Join("、", open))}" + (GasBusy ? $" · {GasTask}" : ""));
    }

    // ============ 急停各部分回应时刻（只写事件文件）============

    /// <summary>包一层计时：记录该部分停止指令的下发时刻与回应时刻（成功 / 失败）。</summary>
    private Func<Task<bool>>? TimedEstopOp(string part, string round, Func<Task<bool>>? op, Func<string>? extra = null)
    {
        if (op == null) return null;
        return async () =>
        {
            var t0 = DateTimeOffset.Now;
            bool ok = false;
            try { ok = await op(); return ok; }
            finally
            {
                var t1 = DateTimeOffset.Now;
                LogFileEvent(ok ? "info" : "err", "系统", $"急停 · {part}{(ok ? "已确认" : "未确认")}",
                    $"{round} · 下发 {Csv.Time(t0)} → 回应 {Csv.Time(t1)}（{Fx((t1 - t0).TotalMilliseconds, "0")} ms）" + (extra?.Invoke() ?? ""));
            }
        };
    }

    // ============ 每 Tick 一行运行记录（P0-1）============

    /// <summary>搅拌驱动器最近一次读到的报警码（null=尚未读到）。</summary>
    private ushort? _stirAlarmCode;

    private void LogTelemetry(bool realT, bool realP) => Logger.Telemetry(BuildTelemetryRow(realT, realP));

    /// <summary>按当前状态组一行运行记录（不入队）。</summary>
    private TelemetryRow BuildTelemetryRow(bool realT, bool realP)
    {
        var row = new TelemetryRow { Time = DateTimeOffset.Now, Sim = HardwareOptions.Sim };
        var ps = _press.Last;
        bool pValid = realP && ps.At != DateTimeOffset.MinValue;
        for (int i = 0; i < 8 && i < Reactors.Count; i++)
        {
            var c = Reactors[i];
            var tc = Temp.Channels[i];
            var t = row.Ch[i];
            if (realT && tc.Online)   // 离线时 PV / 输出 / 模式都是旧值：留空
            {
                t.Pv = tc.Pv;
                t.PvRaw = tc.PvRaw;
                t.OutputPct = tc.OutputPct;
                t.At = tc.Mode >= 0 ? tc.Mode : null;
            }
            t.SpWritten = tc.SpKnown ? tc.Sp : null;   // 开机后软件尚未写过 SP 时留空（仪表 SV 不回读）
            t.TargetSp = c.TSp;
            t.Approach = c.Approach.Phase.ToString();
            t.ApproachSp = c.Approach.Active ? c.Approach.CurrentSp : null;
            t.CurveSp = c.SpMode == "curve" && c.Profile.Running ? c.TSp : null;   // Tick 已按曲线更新 TSp（不调 CurrentTarget，它有副作用）
            if (pValid)
            {
                t.PressRaw = ps.Raw[i];
                t.PressPsi = ps.Psi[i];
            }
            t.State = c.State.ToString();
            t.OverTemp = _otGuard.IsTripped(i);
            t.Valve = c.Valve;
        }
        if (realT)
        {
            row.Srun = Temp.Srun >= 0 ? Temp.Srun : null;
            row.Ctn = Temp.Ctn >= 0 ? Temp.Ctn : null;
        }
        row.StirOn = StirOn;
        row.StirRpmSet = StirRpm;
        row.StirRampMs = _stir.RampMs;
        if (HardwareOptions.UseRealStir && _stir.Online)
        {
            row.StirAlarm = _stirAlarmCode;
            row.BusVoltage = StirBusVoltage > 0 ? StirBusVoltage : null;
        }
        for (int g = 0; g < 3 && g < GasInlets.Count; g++) row.Gas[g] = GasInlets[g].On;
        row.Vent = VentOn;
        row.Estopped = Estopped;
        row.Temp = TelemetryModule.From(HardwareOptions.UseRealTemp, Temp.Online, Temp.Stats);
        row.Temp.LastOkAt = Temp.ValuesAt;   // 与本行 PV / 输出 / At 同属一轮轮询（Stats 的时刻可能已是下一轮）
        row.Press = TelemetryModule.From(HardwareOptions.UseRealAnalog, _press.Online, _press.Stats);
        row.Io = TelemetryModule.From(RealIo, _valves.Online, _valves.Stats);
        row.Stir = TelemetryModule.From(HardwareOptions.UseRealStir, _stir.Online, _stir.Stats);
        return row;
    }
}
