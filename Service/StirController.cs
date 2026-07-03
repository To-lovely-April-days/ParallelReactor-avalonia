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
    private bool _on;
    private int _rpm;
    private DateTime _lastCommReport = DateTime.MinValue;

    public bool On => _on;
    public int Rpm => _rpm;

    /// <summary>加减速时间（ms/1000rpm）：起停平缓度。越大起停越缓。启停/改速时随之下发。</summary>
    public int RampMs { get; set; } = 1000;

    /// <summary>串口通讯异常时触发（节流到每 5s 最多一次）。在 UI 线程回调。</summary>
    public event Action<string>? CommError;

    public StirController(StepperDriver drive) => _drive = drive;

    /// <summary>启动搅拌：复位报警 → 速度模式一次触发，电机持续匀速运转。</summary>
    public async Task StartAsync(int rpm)
    {
        _rpm = rpm;
        _on = true;
        try
        {
            await _drive.ResetAlarmAsync();
            await _drive.RunVelocityAsync(rpm, RampMs, RampMs);
        }
        catch (Exception ex) { ReportComm(ex); }
    }

    /// <summary>停止搅拌：速度模式减速停车（0x6002 急停）。</summary>
    public void Stop()
    {
        _on = false;
        _ = StopInternalAsync();
    }

    private async Task StopInternalAsync()
    {
        try { await _drive.StopVelocityAsync(); } catch (Exception ex) { ReportComm(ex); }
    }

    /// <summary>运行中变更转速：重新触发速度路径，驱动器平滑过渡到新转速。</summary>
    public async Task SetRpmAsync(int rpm)
    {
        _rpm = rpm;
        if (!_on) return;
        try { await _drive.RunVelocityAsync(rpm, RampMs, RampMs); } catch (Exception ex) { ReportComm(ex); }
    }

    /// <summary>设置电机峰值电流（A，驱动器硬夹到 0.3–3.2）并存 EEPROM。返回实际写入的电流，通讯失败返回 null。</summary>
    public async Task<double?> SetCurrentAsync(double amps)
    {
        try { return await _drive.SetPeakCurrentAsync(amps); }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>设置待机电流百分比（0–100）并存 EEPROM。返回实际写入值，通讯失败返回 null。</summary>
    public async Task<int?> SetStandbyPctAsync(int pct)
    {
        try { return await _drive.SetStandbyPctAsync(pct); }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>读回驱动器当前设定的峰值电流（A）= 步进电机恒流运行电流；通讯失败返回 null。</summary>
    public async Task<double?> ReadCurrentAsync()
    {
        try { return await _drive.ReadPeakCurrentAsync(); }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>设置细分（指令脉冲数/转）并存 EEPROM。返回实际写入值，通讯失败返回 null。</summary>
    public async Task<int?> SetMicrostepAsync(int ppr)
    {
        try { return await _drive.SetMicrostepAsync(ppr); }
        catch (Exception ex) { ReportComm(ex); return null; }
    }

    /// <summary>读回当前细分（指令脉冲数/转）；通讯失败返回 null。</summary>
    public async Task<int?> ReadMicrostepAsync()
    {
        try { return await _drive.ReadMicrostepAsync(); }
        catch { return null; }
    }

    /// <summary>读报警码（0=无故障）。用于周期轮询，失败时静默返回 null（不弹通讯错，避免刷屏）。</summary>
    public async Task<ushort?> ReadAlarmAsync()
    {
        try { return await _drive.ReadAlarmAsync(); }
        catch { return null; }
    }

    /// <summary>清除当前报警（0x1801=0x1111）。</summary>
    public async Task ClearAlarmAsync()
    {
        try { await _drive.ResetAlarmAsync(); }
        catch (Exception ex) { ReportComm(ex); }
    }

    private void ReportComm(Exception ex)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCommReport).TotalSeconds < 5) return;
        _lastCommReport = now;
        Dispatcher.UIThread.Post(() => CommError?.Invoke(ex.Message));
    }
}
