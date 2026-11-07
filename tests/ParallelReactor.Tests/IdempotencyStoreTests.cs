using System;
using System.Threading.Tasks;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>§13.3 幂等表：新键 / 重放 / mismatch / in-progress、10 分钟 TTL、1000 上限、必填路径表。</summary>
public class IdempotencyStoreTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_Replay_Mismatch_InProgress()
    {
        var s = new IdempotencyStore();
        string h = IdempotencyStore.Hash("POST", "/api/v1/run", "{}");
        Assert.Equal(IdemBegin.New, s.Begin("k1", h, "k1", T0, out var e));
        Assert.Equal(IdemBegin.InProgress, s.Begin("k1", h, "k1", T0.AddSeconds(1), out var e2));
        Assert.Same(e, e2);
        Assert.Equal(IdemBegin.Mismatch, s.Begin("k1", IdempotencyStore.Hash("POST", "/api/v1/run", "{\"x\":1}"), "k1", T0, out _));
        s.Complete(e, 202, "{\"ok\":true}");
        Assert.Equal(IdemBegin.Replay, s.Begin("k1", h, "k1", T0.AddMinutes(9), out var e3));
        Assert.Equal(202, e3.Status);
        Assert.Equal("{\"ok\":true}", e3.Body);
        Assert.True(e3.Completed.Task.IsCompleted);
        // 同键不同路径也是 mismatch
        Assert.Equal(IdemBegin.Mismatch, s.Begin("k1", IdempotencyStore.Hash("POST", "/api/v1/leak-tests", "{}"), "k1", T0, out _));
    }

    [Fact]
    public void Ttl_Expires_After10Minutes()
    {
        var s = new IdempotencyStore();
        string h = IdempotencyStore.Hash("POST", "/x", "");
        s.Begin("k", h, "k", T0, out var e);
        s.Complete(e, 200, "a");
        Assert.Equal(IdemBegin.Replay, s.Begin("k", h, "k", T0.AddMinutes(10), out _));
        Assert.Equal(IdemBegin.New, s.Begin("k", h, "k", T0.AddMinutes(10).AddSeconds(1), out var fresh));
        Assert.False(fresh.Done);
    }

    [Fact]
    public void Abandon_RemovesEntry()
    {
        var s = new IdempotencyStore();
        string h = IdempotencyStore.Hash("POST", "/x", "");
        s.Begin("k", h, "k", T0, out var e);
        s.Abandon(e);
        Assert.Equal(0, s.Count);
        Assert.Equal(IdemBegin.New, s.Begin("k", h, "k", T0, out _));
    }

    [Fact]
    public void Capacity_EvictsOldest()
    {
        var s = new IdempotencyStore();
        for (int i = 0; i < IdempotencyStore.Capacity + 10; i++)
        {
            s.Begin("k" + i, "h", "k" + i, T0.AddMilliseconds(i), out var e);
            s.Complete(e, 200, "");
        }
        Assert.True(s.Count <= IdempotencyStore.Capacity, $"count={s.Count}");
        // 最早的被淘汰、最新的仍在（都在 TTL 内）
        Assert.Equal(IdemBegin.New, s.Begin("k0", "h", "k0", T0.AddSeconds(5), out _));
        Assert.Equal(IdemBegin.Replay, s.Begin("k" + (IdempotencyStore.Capacity + 9), "h", "x", T0.AddSeconds(5), out _));
    }

    [Fact]
    public async Task InProgress_Waiter_IsReleasedOnComplete()
    {
        var s = new IdempotencyStore();
        s.Begin("k", "h", "k", T0, out var e);
        var waiter = e.Completed.Task;
        Assert.False(waiter.IsCompleted);
        s.Complete(e, 200, "done");
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Abandon_WakesWaiter_WithCancellation()
    {
        var s = new IdempotencyStore();
        s.Begin("k", "h", "k", T0, out var e);
        var waiter = e.Completed.Task;
        s.Abandon(e);
        Assert.True(waiter.IsCanceled);
        Assert.Equal(IdemBegin.New, s.Begin("k", "h", "k2", T0, out _));
    }

    [Fact]
    public void ScopedKey_IsolatesTokens()
    {
        var s = new IdempotencyStore();
        string h = IdempotencyStore.Hash("POST", "/x", "{}");
        s.Begin(IdempotencyStore.ScopedKey("tokA", "k"), h, "k", T0, out var e);
        s.Complete(e, 200, "a");
        // 另一令牌同键同体：是新键，不重放、也不 mismatch
        Assert.Equal(IdemBegin.New, s.Begin(IdempotencyStore.ScopedKey("tokB", "k"), h, "k", T0, out _));
        Assert.Equal(IdemBegin.Replay, s.Begin(IdempotencyStore.ScopedKey("tokA", "k"), h, "k", T0, out _));
    }
}
