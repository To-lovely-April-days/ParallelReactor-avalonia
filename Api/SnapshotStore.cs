using System;
using System.Threading;

namespace ParallelReactor.Api;

/// <summary>
/// 最新快照的持有者：UI 线程在 Tick 末尾 <see cref="Publish"/>（只做一次 volatile 写 + 触发事件），
/// 任意线程读 <see cref="Current"/>。订阅者（WsHub）在事件回调里只能做 TryWrite 入队，绝不能阻塞。
/// </summary>
public sealed class SnapshotStore
{
    private SnapshotBundle? _cur;

    /// <summary>最新快照（尚未产生第一个 Tick 时为 null）。</summary>
    public SnapshotBundle? Current => Volatile.Read(ref _cur);

    /// <summary>每次发布后触发（在发布者线程上）。</summary>
    public event Action<SnapshotBundle>? Published;

    public void Publish(SnapshotBundle b)
    {
        Volatile.Write(ref _cur, b);
        Published?.Invoke(b);
    }
}
