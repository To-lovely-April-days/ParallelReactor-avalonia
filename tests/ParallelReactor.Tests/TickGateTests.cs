using System;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C1：控制循环 Tick 的防重入与超时统计。</summary>
public class TickGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1200);
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NestedEnter_IsRejectedAndCounted()
    {
        var g = new TickGate(Interval);
        Assert.True(g.TryEnter());
        Assert.True(g.Inside);
        Assert.False(g.TryEnter());
        Assert.False(g.TryEnter());
        Assert.Equal(2, g.Skipped);
        g.Exit(TimeSpan.FromMilliseconds(10), T0);
        Assert.False(g.Inside);
        Assert.True(g.TryEnter());   // Exit 之后可以再次进入
        Assert.Equal(2, g.Entered);
    }

    [Fact]
    public void Overrun_CountedAndMaxTracked()
    {
        var g = new TickGate(Interval);
        g.TryEnter(); Assert.False(g.Exit(TimeSpan.FromMilliseconds(1200), T0));   // 等于节拍不算超时
        Assert.Equal(0, g.Overruns);
        g.TryEnter(); Assert.True(g.Exit(TimeSpan.FromMilliseconds(1500), T0));
        Assert.Equal(1, g.Overruns);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), g.LastOverrun);
        g.TryEnter(); g.Exit(TimeSpan.FromMilliseconds(30), T0);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), g.MaxTook);
        Assert.Equal(TimeSpan.FromMilliseconds(30), g.LastTook);
    }

    [Fact]
    public void OverrunLog_RateLimitedToOncePerMinute()
    {
        var g = new TickGate(Interval);
        var slow = TimeSpan.FromSeconds(2);
        g.TryEnter(); Assert.True(g.Exit(slow, T0));
        g.TryEnter(); Assert.False(g.Exit(slow, T0.AddSeconds(30)));
        g.TryEnter(); Assert.False(g.Exit(slow, T0.AddSeconds(59)));
        g.TryEnter(); Assert.True(g.Exit(slow, T0.AddSeconds(60)));
        Assert.Equal(4, g.Overruns);   // 计数不受日志限流影响
    }
}
