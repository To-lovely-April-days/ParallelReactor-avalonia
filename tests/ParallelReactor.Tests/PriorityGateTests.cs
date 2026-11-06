using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Hardware;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>总线优先级互斥：紧急事务插到普通事务之前，同级 FIFO，在途事务不被打断，任何时刻只有一个持有者。</summary>
public class PriorityGateTests
{
    [Fact]
    public async Task FreeGate_EntersImmediately_ReleaseFrees()
    {
        var g = new PriorityGate();
        var t = g.EnterAsync();
        Assert.True(t.IsCompleted);
        Assert.True(g.IsHeld);
        g.Release();
        Assert.False(g.IsHeld);
        await g.EnterAsync(urgent: true);
        Assert.True(g.IsHeld);
        g.Release();
        Assert.False(g.IsHeld);
    }

    [Fact]
    public async Task UrgentJumpsQueuedNormals_ThenNormalsFifo()
    {
        var g = new PriorityGate();
        await g.EnterAsync();   // 在途事务
        var order = new List<string>();
        var n1 = g.EnterAsync().ContinueWith(_ => { lock (order) order.Add("n1"); });
        var n2 = g.EnterAsync().ContinueWith(_ => { lock (order) order.Add("n2"); });
        var n3 = g.EnterAsync().ContinueWith(_ => { lock (order) order.Add("n3"); });
        var u1 = g.EnterAsync(urgent: true).ContinueWith(_ => { lock (order) order.Add("u1"); });
        Assert.Equal((1, 3), g.Waiting);
        Assert.Empty(order);   // 在途事务未结束：谁也拿不到

        g.Release(); await u1;
        g.Release(); await n1;
        g.Release(); await n2;
        g.Release(); await n3;
        g.Release();
        Assert.Equal(new[] { "u1", "n1", "n2", "n3" }, order);
        Assert.False(g.IsHeld);
    }

    /// <summary>
    /// 急停快速路径的「紧急一遍 + 普通一遍」：按下急停前已排队的普通帧（如开阀）会落在紧急停止帧之后，
    /// 但之后再以普通优先级排入的停止帧一定在它之后——最后落到设备上的是停止帧。
    /// </summary>
    [Fact]
    public async Task NormalCopyAfterUrgent_LandsAfterStaleQueuedNormal()
    {
        var g = new PriorityGate();
        await g.EnterAsync();   // 在途：一次轮询
        var order = new List<string>();
        var stale = g.EnterAsync().ContinueWith(_ => { lock (order) order.Add("stale-open"); });
        var urgent = g.EnterAsync(urgent: true).ContinueWith(_ => { lock (order) order.Add("urgent-stop"); });
        g.Release(); await urgent;            // 在途结束：紧急停止帧先拿到总线
        // 紧急帧做完后快速路径再排一遍普通停止帧（此时旧开阀帧仍在普通队列里，排在它前面）
        var copy = g.EnterAsync().ContinueWith(_ => { lock (order) order.Add("normal-stop"); });
        g.Release(); await stale;
        g.Release(); await copy;
        g.Release();
        Assert.Equal(new[] { "urgent-stop", "stale-open", "normal-stop" }, order);
        Assert.Equal("normal-stop", order[^1]);
    }

    [Fact]
    public async Task Urgents_AreFifoAmongThemselves()
    {
        var g = new PriorityGate();
        await g.EnterAsync();
        var order = new List<string>();
        var n1 = g.EnterAsync().ContinueWith(_ => { lock (order) order.Add("n1"); });
        var u1 = g.EnterAsync(true).ContinueWith(_ => { lock (order) order.Add("u1"); });
        g.Release(); await u1;
        // u1 持有期间又来一个紧急、一个普通
        var u2 = g.EnterAsync(true).ContinueWith(_ => { lock (order) order.Add("u2"); });
        var n2 = g.EnterAsync().ContinueWith(_ => { lock (order) order.Add("n2"); });
        g.Release(); await u2;
        g.Release(); await n1;
        g.Release(); await n2;
        g.Release();
        Assert.Equal(new[] { "u1", "u2", "n1", "n2" }, order);
    }

    [Fact]
    public async Task CancelledBeforeEnter_Throws_GateUnchanged()
    {
        var g = new PriorityGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() => g.EnterAsync(false, cts.Token));
        Assert.False(g.IsHeld);
    }

    [Fact]
    public async Task Stress_NeverTwoHolders()
    {
        var g = new PriorityGate();
        int holders = 0, maxHolders = 0, done = 0;
        var errors = new ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            await g.EnterAsync(urgent: i % 7 == 0);
            int h = Interlocked.Increment(ref holders);
            if (h > 1) errors.Add($"holders={h}");
            InterlockedMax(ref maxHolders, h);
            await Task.Yield();
            Interlocked.Decrement(ref holders);
            Interlocked.Increment(ref done);
            g.Release();
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Empty(errors);
        Assert.Equal(1, maxHolders);
        Assert.Equal(200, done);
        Assert.False(g.IsHeld);
    }

    private static void InterlockedMax(ref int target, int v)
    {
        int cur;
        while ((cur = Volatile.Read(ref target)) < v && Interlocked.CompareExchange(ref target, v, cur) != cur) { }
    }
}
