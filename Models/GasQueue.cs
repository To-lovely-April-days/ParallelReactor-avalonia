using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>
/// 程序气路步骤的先到先得等待队列（纯逻辑，只在 UI 线程使用）。气路（共用总管）同一时间只由一个程序占用：
/// 另一程序的吹扫 / 升压 / 排空 / 稳压脉冲到来时入队，排到队首且气路空闲才取租约；等待超过上限由调用方按步骤故障处理。
/// 同一通道同一时间只有一张票（重复入队返回已有票）。
/// 时刻一律用<b>单调时间</b>（Stopwatch 流逝量，调用方传入），不受面板 NTP 校时跳变影响。
/// </summary>
public sealed class GasQueue
{
    private readonly List<(long Ticket, int Rv, TimeSpan Since)> _q = new();
    private long _next;

    /// <summary>入队，返回票号；该通道已在队中则返回原票（不改入队时刻）。</summary>
    public long Enqueue(int rv, TimeSpan now)
    {
        foreach (var e in _q) if (e.Rv == rv) return e.Ticket;
        long t = ++_next;
        _q.Add((t, rv, now));
        return t;
    }

    public int Count => _q.Count;

    /// <summary>该票是否排在队首。</summary>
    public bool IsHead(long ticket) => _q.Count > 0 && _q[0].Ticket == ticket;

    /// <summary>出队（取到租约、放弃等待、被中止都调用；不在队中忽略）。</summary>
    public bool Remove(long ticket)
    {
        int i = _q.FindIndex(e => e.Ticket == ticket);
        if (i < 0) return false;
        _q.RemoveAt(i);
        return true;
    }

    public bool Contains(int rv) => _q.Any(e => e.Rv == rv);

    /// <summary>已等待时长（不在队中为 0）。</summary>
    public TimeSpan Waited(long ticket, TimeSpan now)
    {
        int i = _q.FindIndex(e => e.Ticket == ticket);
        return i < 0 ? TimeSpan.Zero : now - _q[i].Since;
    }

    /// <summary>等待是否已超过上限（max ≤ 0 视为不限）。</summary>
    public bool TimedOut(long ticket, TimeSpan now, TimeSpan max)
        => max > TimeSpan.Zero && Waited(ticket, now) >= max;

    /// <summary>等待中的通道号（按先后顺序）。</summary>
    public IReadOnlyList<int> Waiting => _q.Select(e => e.Rv).ToList();

    public void Clear() => _q.Clear();
}
