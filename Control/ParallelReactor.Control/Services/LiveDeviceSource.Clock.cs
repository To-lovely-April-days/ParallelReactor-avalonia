using System;
using ParallelReactor.Api;
using ParallelReactor.Client;

namespace ParallelReactor.Control.Services;

/// <summary>
/// LiveDeviceSource 的设备时钟（M5 复核）：HMI 给的时刻（帧、事件、程序的 startedAt）都是设备时钟，本机的是 PC 时钟，两边不一定齐。
/// <list type="bullet">
/// <item><b>按帧估的时钟差</b>（<see cref="FrameClockOffset"/>）：WS hello 只量一次（Client 的 ServerClockOffset），一条 WS 连接可以连几天，期间两边的钟会漂、会被校时 ——
///   下发时刻要换到设备时钟（程序台账按它判断「比下发新的执行」），所以按最近的帧另外估一个；看到下发命令自己的事件之后台账换成那条事件的时刻。</item>
/// <item><b>记下的 hello 时钟差</b>（<see cref="_serverOffset"/>）：断开 / 改地址时连接对象先没了，曲线的断开标记与横轴的「现在」照样按设备时钟
///   （复核：以前退回到 0 —— HMI 的钟慢几个小时时一个断开标记就把 4 h 的曲线全删掉、断开之后横轴错开）。换了设备地址时清掉。</item>
/// </list>
/// </summary>
public sealed partial class LiveDeviceSource
{
    /// <summary>按帧估时钟差用最近多少帧（按帧数、不按本机时刻：本机或设备的钟被校时跳了，这么多帧之后就跟上）。</summary>
    public const int ClockSamples = 32;

    private readonly TimeSpan[] _clockSamples = new TimeSpan[ClockSamples];
    private int _clockNext, _clockCount;
    /// <summary>最近一次知道的 WS hello 时钟差（设备 − 本机，同一个连接目标）：连接对象没了的时候曲线照样用它。</summary>
    private TimeSpan? _serverOffset;

    /// <summary>每一帧：记一个样本（帧的时刻 − 收到时的本机时刻 = 时钟差 − 这一帧路上的延迟），顺手记下这条连接的 hello 时钟差。</summary>
    private void NoteClock(TelemetrySnapshot f)
    {
        if (Connection?.Ws?.ServerClockOffset is { } hello) _serverOffset = hello;
        if (f.Ts == default) return;
        var ts = f.Ts.Kind == DateTimeKind.Utc ? f.Ts : f.Ts.Kind == DateTimeKind.Local ? f.Ts.ToUniversalTime() : DateTime.SpecifyKind(f.Ts, DateTimeKind.Utc);
        _clockSamples[_clockNext] = ts - _clock.UtcNow;
        _clockNext = (_clockNext + 1) % ClockSamples;
        if (_clockCount < ClockSamples) _clockCount++;
    }

    /// <summary>
    /// 按帧估的时钟差（设备 − 本机）：最近 <see cref="ClockSamples"/> 帧的样本里最大的那个。每个样本 = 真实时钟差 − 这一帧在网络、HMI 的发送队列、
    /// 本机 UI 线程上耽搁的时间（≥ 0），耽搁最少的那一帧最接近真实值（同 NTP 的最小延迟滤波）：取最大值，排队、UI 线程忙都只让估计偏小几毫秒到几十毫秒，
    /// 不会像几天前的 hello 那样差出几秒（台账的容差 2 s）。还没有帧为 null。
    /// </summary>
    public TimeSpan? FrameClockOffset
    {
        get
        {
            if (_clockCount == 0) return null;
            var max = _clockSamples[0];
            for (int i = 1; i < _clockCount; i++) if (_clockSamples[i] > max) max = _clockSamples[i];
            return max;
        }
    }

    /// <summary>下发时刻换到设备时钟用的时钟差：按帧估的 → 这条连接 hello 的 → 记下的 → 不知道（null：台账只按下发时看到的上一次执行判断新旧）。</summary>
    private TimeSpan? DownloadClockOffset(IReactorConnection c) => FrameClockOffset ?? c.Ws?.ServerClockOffset ?? _serverOffset;

    /// <summary>曲线（断开标记、横轴的「现在」）用的时钟差：这条连接 hello 的 → 记下的（连接对象已经没了、新连接还没 hello）→ 0。</summary>
    private TimeSpan HistoryClockOffset => Connection?.Ws?.ServerClockOffset ?? _serverOffset ?? TimeSpan.Zero;

    /// <summary>换了设备地址：之前的时钟差（hello 的、按帧估的）都不再算数 —— 新地址后面可能是另一台设备。</summary>
    private void ForgetClock()
    {
        _serverOffset = null;
        ForgetFrameClock();
    }

    /// <summary>按帧估的样本清掉（HMI 重启：它的钟可能在启动时校过，旧样本不再代表现在的时钟差）。</summary>
    private void ForgetFrameClock()
    {
        _clockCount = 0;
        _clockNext = 0;
    }
}
