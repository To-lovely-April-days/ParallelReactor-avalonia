using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Client;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>手动推进的时钟：Delay 挂起到 <see cref="Advance"/> 越过到期时刻。</summary>
public sealed class FakeClock : IClock
{
    private readonly object _lock = new();
    private readonly List<(DateTime Due, TaskCompletionSource Tcs)> _waits = new();
    private DateTime _now;

    public FakeClock(DateTime? start = null) => _now = start ?? new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow { get { lock (_lock) return _now; } }
    public int Pending { get { lock (_lock) return _waits.Count; } }

    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;
            _waits.Add((_now + delay, tcs));
        }
        if (ct.CanBeCanceled)
            ct.Register(() => { lock (_lock) _waits.RemoveAll(w => w.Tcs == tcs); tcs.TrySetCanceled(ct); });
        return tcs.Task;
    }

    /// <summary>推进时间并唤醒到期的等待（按到期先后）。</summary>
    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource> fire;
        lock (_lock)
        {
            _now += by;
            fire = _waits.Where(w => w.Due <= _now).OrderBy(w => w.Due).Select(w => w.Tcs).ToList();
            _waits.RemoveAll(w => w.Due <= _now);
        }
        foreach (var t in fire) t.TrySetResult();
    }

    /// <summary>推进并让续体跑一会儿。</summary>
    public async Task AdvanceAsync(TimeSpan by, int settleMs = 50)
    {
        Advance(by);
        await Task.Delay(settleMs);
    }

    /// <summary>等待条件成立（真实时间上限），轮询 5 ms。</summary>
    public static async Task WaitUntil(Func<bool> cond, int timeoutMs = 3000, string? what = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待超时：" + (what ?? "条件"));
            await Task.Delay(5);
        }
    }
}
