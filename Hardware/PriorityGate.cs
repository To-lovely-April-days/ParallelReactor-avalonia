using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Hardware;

/// <summary>
/// 带优先级的总线互斥（纯逻辑，单元测试直接链接）：同一时间只有一个持有者（半双工一问一答）。
/// 释放时先交给等待中的<b>紧急</b>请求（急停帧），没有时再交给普通请求；同一优先级内先来先得（FIFO）。
/// 在途事务不会被打断——紧急帧最多等当前这一笔事务结束（正常约几十 ms，超时约 300–600 ms）。
/// </summary>
public sealed class PriorityGate
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _urgent = new();
    private readonly Queue<TaskCompletionSource> _normal = new();
    private bool _held;

    /// <summary>当前是否有持有者。</summary>
    public bool IsHeld { get { lock (_lock) return _held; } }

    /// <summary>排队等待的请求数（紧急, 普通）。</summary>
    public (int Urgent, int Normal) Waiting { get { lock (_lock) return (_urgent.Count, _normal.Count); } }

    /// <summary>进入：空闲时立即完成，否则排到对应优先级队尾。完成后调用方必须 <see cref="Release"/> 一次。</summary>
    public Task EnterAsync(bool urgent = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }
            // 异步续体：Release 里 SetResult 不会在释放者的线程上同步跑下一笔事务
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (urgent ? _urgent : _normal).Enqueue(tcs);
            return tcs.Task;
        }
    }

    /// <summary>释放：直接把持有权交给下一位（紧急优先），没有等待者时置空闲。</summary>
    public void Release()
    {
        TaskCompletionSource? next = null;
        lock (_lock)
        {
            if (_urgent.Count > 0) next = _urgent.Dequeue();
            else if (_normal.Count > 0) next = _normal.Dequeue();
            else _held = false;
        }
        next?.SetResult();
    }
}
