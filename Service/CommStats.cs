using System;
using System.Threading;

namespace ParallelReactor.Services;

/// <summary>
/// 单个通讯模块的累计统计：事务（轮询）次数、失败次数、最近一次成功响应的时刻。
/// 各服务的轮询 / 事务在线程池线程上完成，计数用 Interlocked，读写互不加锁。
/// 写入运行记录（telemetry CSV）用于事后分析掉线率。纯逻辑（无 UI 依赖）。
/// </summary>
public sealed class CommStats
{
    private long _polls, _fails;
    private long _lastOkTicks;    // UTC ticks，0 = 尚无成功响应
    private int _lastOkOffsetMin; // 本地时区偏移（分钟），与 _lastOkTicks 同时写

    /// <summary>累计事务（轮询）次数，含失败。</summary>
    public long Polls => Interlocked.Read(ref _polls);

    /// <summary>累计失败次数。</summary>
    public long Fails => Interlocked.Read(ref _fails);

    /// <summary>最近一次成功响应到达的时刻（本地时间带偏移）；尚无成功响应时为 null。</summary>
    public DateTimeOffset? LastOkAt
    {
        get
        {
            long t = Interlocked.Read(ref _lastOkTicks);
            if (t == 0) return null;
            var off = TimeSpan.FromMinutes(Volatile.Read(ref _lastOkOffsetMin));
            return new DateTimeOffset(t + off.Ticks, off);
        }
    }

    /// <summary>记一次成功事务（响应到达时调用）。</summary>
    public void Ok() => Ok(DateTimeOffset.Now);

    public void Ok(DateTimeOffset at)
    {
        Interlocked.Increment(ref _polls);
        Volatile.Write(ref _lastOkOffsetMin, (int)at.Offset.TotalMinutes);
        Interlocked.Exchange(ref _lastOkTicks, at.UtcTicks);
    }

    /// <summary>记一次失败事务。</summary>
    public void Fail()
    {
        Interlocked.Increment(ref _polls);
        Interlocked.Increment(ref _fails);
    }
}
