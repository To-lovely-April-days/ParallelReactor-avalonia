using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 全局搅拌控制器：把界面的「开 / 关 / 转速」映射到雷赛 DM2C 步进驱动器（全机共用一台搅拌电机）。
/// <para>
/// DM2C 的速度运行靠 JOG 保活：写 0x1801=0x4001(正转) <b>必须每 ≤50ms 重发一次</b>才连续运转，停发即停。
/// 为保证连续转，保活放在<b>独立线程的连续循环</b>里：发一拍 → 短延时 → 再发，背靠背补脉冲
/// （间隔≈串口往返+10ms，稳定 &lt;50ms），避免 UI 线程卡顿/串口延迟造成的"转一下停一下"。
/// </para>
/// 通讯异常被吞掉并节流上报；单次脉冲失败不致命，下一拍会重发。
/// </summary>
public sealed class StirController
{
    private const int GapMs = 10;   // 每拍之间的延时；加上串口往返后总间隔仍 <50ms

    private readonly StepperDriver _drive;
    private CancellationTokenSource? _cts;
    private bool _on;
    private int _rpm;
    private DateTime _lastCommReport = DateTime.MinValue;

    public bool On => _on;
    public int Rpm => _rpm;

    /// <summary>串口通讯异常时触发（节流到每 5s 最多一次）。在 UI 线程回调。</summary>
    public event Action<string>? CommError;

    public StirController(StepperDriver drive) => _drive = drive;

    /// <summary>启动搅拌：复位报警 → 下发转速 → 开始连续 JOG 保活循环。</summary>
    public async Task StartAsync(int rpm)
    {
        _rpm = rpm;
        _on = true;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            await _drive.ResetAlarmAsync();
            await _drive.SetSpeedAsync(rpm);
        }
        catch (Exception ex) { ReportComm(ex); }
        _ = KeepAliveLoop(cts.Token);
    }

    /// <summary>停止搅拌：取消保活循环，驱动器因收不到 JOG 脉冲而自动停转。</summary>
    public void Stop()
    {
        _on = false;
        _cts?.Cancel();
    }

    /// <summary>运行中变更转速，立即下发。</summary>
    public async Task SetRpmAsync(int rpm)
    {
        _rpm = rpm;
        if (!_on) return;
        try { await _drive.SetSpeedAsync(rpm); } catch (Exception ex) { ReportComm(ex); }
    }

    /// <summary>连续保活：尽快地反复补发 JOG 脉冲，间隔稳定小于 50ms，保证电机连续运转。</summary>
    private async Task KeepAliveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _drive.KeepRunForwardAsync().ConfigureAwait(false); }
            catch (Exception ex) { ReportComm(ex); }
            try { await Task.Delay(GapMs, ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    private void ReportComm(Exception ex)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCommReport).TotalSeconds < 5) return;
        _lastCommReport = now;
        Dispatcher.UIThread.Post(() => CommError?.Invoke(ex.Message));
    }
}
