using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 全局搅拌控制器：把界面的「开 / 关 / 转速」映射到雷赛 DM2C 步进驱动器（全机共用一台搅拌电机）。
/// <para>
/// DM2C 的速度运行靠 JOG 保活：写 0x1801=0x4001(正转) <b>必须每 ≤50ms 重发一次</b>才连续运转，停发即停。
/// 因此本控制器用一个 40ms 的定时器持续补发保活脉冲；停止时只要停掉定时器，驱动器自然停转。
/// 转速变更立即下发；另用 1s 定时器轮询报警字，用于故障提示。
/// </para>
/// 所有通讯异常都被吞掉——单次脉冲失败不致命，下一拍会重发。
/// </summary>
public sealed class StirController
{
    private const int KeepAliveMs = 40;   // <50ms，满足 DM2C JOG 保活要求

    private readonly StepperDriver _drive;
    private readonly DispatcherTimer _keepAlive;
    private readonly DispatcherTimer _statusPoll;

    private bool _on;
    private int _rpm;
    private bool _pulsing;   // 防止保活脉冲重入（一拍未发完就到下一拍）
    private ushort _lastAlarm;
    private DateTime _lastCommReport = DateTime.MinValue;

    public bool On => _on;
    public int Rpm => _rpm;

    /// <summary>驱动器报警出现时触发（参数为报警码，0 以外）。在 UI 线程回调。</summary>
    public event Action<ushort>? FaultRaised;

    /// <summary>串口通讯异常时触发（节流到每 5s 最多一次），参数为错误说明。</summary>
    public event Action<string>? CommError;

    private void ReportComm(Exception ex)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCommReport).TotalSeconds < 5) return;
        _lastCommReport = now;
        CommError?.Invoke(ex.Message);
    }

    public StirController(StepperDriver drive)
    {
        _drive = drive;

        _keepAlive = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(KeepAliveMs) };
        _keepAlive.Tick += (_, _) => _ = PulseAsync();

        _statusPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusPoll.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>启动搅拌：复位报警 → 下发转速 → 开始 JOG 保活。</summary>
    public async Task StartAsync(int rpm)
    {
        _rpm = rpm;
        _on = true;
        try
        {
            await _drive.ResetAlarmAsync();
            await _drive.SetSpeedAsync(rpm);
        }
        catch (Exception ex) { ReportComm(ex); /* 由后续保活/轮询重试 */ }
        _keepAlive.Start();
        _statusPoll.Start();
    }

    /// <summary>停止搅拌：停掉保活定时器，驱动器因收不到 JOG 脉冲而自动停转。</summary>
    public void Stop()
    {
        _on = false;
        _keepAlive.Stop();
        _statusPoll.Stop();
    }

    /// <summary>运行中变更转速，立即下发。</summary>
    public async Task SetRpmAsync(int rpm)
    {
        _rpm = rpm;
        if (!_on) return;
        try { await _drive.SetSpeedAsync(rpm); } catch (Exception ex) { ReportComm(ex); }
    }

    private async Task PulseAsync()
    {
        if (!_on || _pulsing) return;
        _pulsing = true;
        try { await _drive.KeepRunForwardAsync(); }
        catch (Exception ex) { ReportComm(ex); /* 单次脉冲失败不致命，下一拍重发 */ }
        finally { _pulsing = false; }
    }

    private async Task PollAsync()
    {
        try
        {
            var alarm = await _drive.ReadAlarmAsync();
            if (alarm != _lastAlarm)
            {
                _lastAlarm = alarm;
                if (alarm != 0)
                {
                    Stop();
                    FaultRaised?.Invoke(alarm);
                }
            }
        }
        catch (Exception ex) { ReportComm(ex); }
    }
}
