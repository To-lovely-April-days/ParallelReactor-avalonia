using System;

namespace ParallelReactor.Services;

/// <summary>
/// 控制循环 Tick 的防重入与超时统计（C1）。纯逻辑，测试工程直接链接本文件。
/// 今天 Tick 是同步方法、DispatcherTimer 本身不会重入；但嵌套消息循环、远程「立即刷新」或测试直接调用
/// Tick() 时可能重入，门闸保证同一时间只有一个 Tick 在执行。
/// </summary>
public sealed class TickGate
{
    private bool _inside;
    private DateTime _lastOverrunLogged = DateTime.MinValue;

    public TickGate(TimeSpan interval, TimeSpan? logEvery = null)
    {
        Interval = interval;
        LogEvery = logEvery ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>Tick 节拍；单次耗时超过它记为一次超时。</summary>
    public TimeSpan Interval { get; }
    /// <summary>超时日志的最小间隔（默认每分钟最多一条）。</summary>
    public TimeSpan LogEvery { get; }

    public long Entered { get; private set; }
    public long Skipped { get; private set; }
    public long Overruns { get; private set; }
    public TimeSpan MaxTook { get; private set; }
    public TimeSpan LastTook { get; private set; }
    /// <summary>最近一次超时的耗时（没有超时为 Zero）。</summary>
    public TimeSpan LastOverrun { get; private set; }
    public bool Inside => _inside;

    /// <summary>已在 Tick 内时返回 false 并累加 <see cref="Skipped"/>。</summary>
    public bool TryEnter()
    {
        if (_inside) { Skipped++; return false; }
        _inside = true;
        Entered++;
        return true;
    }

    /// <summary>记录本次耗时并离开门闸。</summary>
    public void Exit(TimeSpan took) => Exit(took, DateTime.UtcNow);

    /// <summary>记录本次耗时并离开门闸。返回 true 表示本次超时且距上次记录已超过 <see cref="LogEvery"/>（调用方据此写一条事件）。</summary>
    public bool Exit(TimeSpan took, DateTime nowUtc)
    {
        _inside = false;
        LastTook = took;
        if (took > MaxTook) MaxTook = took;
        if (took <= Interval) return false;
        Overruns++;
        LastOverrun = took;
        if (nowUtc - _lastOverrunLogged < LogEvery) return false;
        _lastOverrunLogged = nowUtc;
        return true;
    }
}
