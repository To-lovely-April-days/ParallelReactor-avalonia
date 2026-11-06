using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 全局搅拌控制器：把界面的「开 / 关 / 转速」映射到雷赛 DM2C 步进驱动器（全机共用一台搅拌电机）。
/// <para>
/// 采用 DM2C 的 <b>PR 路径0 速度模式</b>：启动时一次触发，电机即持续、匀速运转，直到急停或改速。
/// 相比旧的 JOG 保活（需每 ≤50ms 重发，9600 波特率下抖动会降级为点动 → 转速忽快忽慢），
/// 速度模式转速恒定、且不再持续占用串口。改速只需重新触发一次速度路径，驱动器平滑过渡到新转速。
/// </para>
/// 通讯异常被吞掉并节流上报。
/// </summary>
public sealed class StirController
{
    private readonly StepperDriver _drive;
    private readonly StepperDriver? _urgentDrive;   // 同一驱动器，走总线紧急视图（急停停车帧插队）
    private bool _on;
    private int _rpm;
    private DateTime _lastCommReport = DateTime.MinValue;
    private int _fails = 3;   // 连续通讯失败次数（初值=离线，首次通讯成功才算在线）

    public bool On => _on;
    public int Rpm => _rpm;

    /// <summary>驱动器是否在线：连续 3 次通讯失败视为离线（未配置串口时恒为离线）。</summary>
    public bool Online => _fails < 3;

    /// <summary>事务统计（计数 = 全部命令与读取事务，含 Tick 每 3 次一轮的报警码 / 母线电压读取）。</summary>
    public CommStats Stats { get; } = new();

    /// <summary>加减速时间（ms/1000rpm）：起停平缓度。越大起停越缓。启停/改速时随之下发。</summary>
    public int RampMs { get; set; } = 1000;

    private volatile bool _locked;

    /// <summary>急停锁定：为 true 时拒绝启动 / 改速（停止不受影响）。volatile：急停快速路径可能在任意线程置位。</summary>
    public bool Locked { get => _locked; set => _locked = value; }

    /// <summary>串口通讯异常时触发（节流到每 5s 最多一次）。在 UI 线程回调。</summary>
    public event Action<string>? CommError;

    public StirController(StepperDriver drive, StepperDriver? urgentDrive = null)
    {
        _drive = drive;
        _urgentDrive = urgentDrive;
    }

    /// <summary>启动搅拌：复位报警 → 速度模式一次触发，电机持续匀速运转。返回是否下发成功（急停锁定时返回 false）。</summary>
    public async Task<bool> StartAsync(int rpm)
    {
        _rpm = rpm;
        if (Locked) return false;
        _on = true;
        try
        {
            await _drive.ResetAlarmAsync();
            if (Locked) { _on = false; return false; }
            await _drive.RunVelocityAsync(rpm, RampMs, RampMs);
            Ok();
        }
        catch (Exception ex) { _on = false; ReportComm(ex); return false; }

        // 启动途中按了急停：启动帧可能排在急停的停止帧之后才落到驱动器，补发一次停止
        if (Locked) { await StopAsync(); return false; }
        return true;
    }

    /// <summary>停止搅拌：速度模式减速停车（0x6002 急停）。</summary>
    public void Stop()
    {
        _on = false;
        _ = StopAsync();
    }

    /// <summary>停止搅拌并等待下发结果（急停用）。返回是否下发成功。
    /// urgent：走总线紧急视图（急停快速路径，可在任意线程调用）。</summary>
    public async Task<bool> StopAsync() => await StopAsync(urgent: false);

    public async Task<bool> StopAsync(bool urgent)
    {
        _on = false;
        try { await (urgent && _urgentDrive != null ? _urgentDrive : _drive).StopVelocityAsync(); Ok(); return true; }
        catch (Exception ex) { ReportComm(ex); return false; }
    }

    /// <summary>运行中变更转速：重新触发速度路径，驱动器平滑过渡到新转速。</summary>
    public async Task SetRpmAsync(int rpm)
    {
        _rpm = rpm;
        if (!_on || Locked) return;
        try { await _drive.RunVelocityAsync(rpm, RampMs, RampMs); Ok(); }
        catch (Exception ex) { ReportComm(ex); }
    }

    /// <summary>设置电机峰值电流（A，驱动器硬夹到 0.3–3.2）并存 EEPROM。返回实际写入的电流，通讯失败返回 null。</summary>
    public async Task<double?> SetCurrentAsync(double amps)
    {
        try { var v = await _drive.SetPeakCurrentAsync(amps); Ok(); return v; }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>设置待机电流百分比（0–100）并存 EEPROM。返回实际写入值，通讯失败返回 null。</summary>
    public async Task<int?> SetStandbyPctAsync(int pct)
    {
        try { var v = await _drive.SetStandbyPctAsync(pct); Ok(); return v; }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>读回驱动器当前设定的峰值电流（A）= 步进电机恒流运行电流；通讯失败返回 null。</summary>
    public async Task<double?> ReadCurrentAsync()
    {
        try { var v = await _drive.ReadPeakCurrentAsync(); Ok(); return v; }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>设置细分（指令脉冲数/转）并存 EEPROM。返回实际写入值，通讯失败返回 null。</summary>
    public async Task<int?> SetMicrostepAsync(int ppr)
    {
        try { var v = await _drive.SetMicrostepAsync(ppr); Ok(); return v; }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>读回当前细分（指令脉冲数/转）；通讯失败返回 null。</summary>
    public async Task<int?> ReadMicrostepAsync()
    {
        try { var v = await _drive.ReadMicrostepAsync(); Ok(); return v; }
        catch { CountFail(); return null; }
    }

    /// <summary>读母线电压（V）；失败静默返回 null。带载电压塌陷=电源容量不足的直接证据。</summary>
    public async Task<double?> ReadBusVoltageAsync()
    {
        try { var v = await _drive.ReadBusVoltageAsync(); Ok(); return v; }
        catch { CountFail(); return null; }
    }

    /// <summary>读报警码（0=无故障）。用于周期轮询，失败时静默返回 null（不弹通讯错，避免刷屏）。</summary>
    public async Task<ushort?> ReadAlarmAsync()
    {
        try { var v = await _drive.ReadAlarmAsync(); Ok(); return v; }
        catch { CountFail(); return null; }
    }

    /// <summary>清除当前报警（0x1801=0x1111）。</summary>
    public async Task ClearAlarmAsync()
    {
        try { await _drive.ResetAlarmAsync(); Ok(); }
        catch (Exception ex) { ReportComm(ex); }
    }

    private void Ok() { _fails = 0; Stats.Ok(); }

    private void CountFail() { Stats.Fail(); if (_fails < 3) _fails++; }

    private void ReportComm(Exception ex)
    {
        CountFail();
        var now = DateTime.UtcNow;
        if ((now - _lastCommReport).TotalSeconds < 5) return;
        _lastCommReport = now;
        Dispatcher.UIThread.Post(() => CommError?.Invoke(ex.Message));
    }
}
