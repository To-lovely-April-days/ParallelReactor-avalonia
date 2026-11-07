using System;
using System.Threading;
using ParallelReactor.Api;

namespace ParallelReactor.Client;

/// <summary>遥测帧环形缓冲（默认 7200 帧 ≈ 2.4 h @1.2 s）。任意线程写入，<see cref="Snapshot"/> 返回不可变副本供曲线绘制。</summary>
public sealed class TelemetryStore
{
    private readonly TelemetrySnapshot?[] _ring;
    private readonly object _lock = new();
    private int _head, _count;
    private TelemetrySnapshot? _latest;

    public TelemetryStore(int capacity = 7200)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new TelemetrySnapshot?[capacity];
    }

    public int Capacity => _ring.Length;
    public int Count { get { lock (_lock) return _count; } }
    public TelemetrySnapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>加入一帧；seq 不大于最新帧的重复帧忽略（重连 / 重同步后 GET /snapshot 与 WS 帧可能重叠）。返回是否采纳。</summary>
    public bool Add(TelemetrySnapshot frame)
    {
        if (frame == null) return false;
        lock (_lock)
        {
            if (_latest != null && frame.Seq <= _latest.Seq && frame.Seq != 0) return false;
            _ring[(_head + _count) % _ring.Length] = frame;
            if (_count < _ring.Length) _count++;
            else _head = (_head + 1) % _ring.Length;
            Volatile.Write(ref _latest, frame);
            return true;
        }
    }

    /// <summary>按时间先后的副本。</summary>
    public TelemetrySnapshot[] Snapshot()
    {
        lock (_lock)
        {
            var a = new TelemetrySnapshot[_count];
            for (int i = 0; i < _count; i++) a[i] = _ring[(_head + i) % _ring.Length]!;
            return a;
        }
    }

    /// <summary>最近 <paramref name="window"/> 内的帧。</summary>
    public TelemetrySnapshot[] Since(DateTime fromUtc)
    {
        var all = Snapshot();
        int i = 0;
        while (i < all.Length && all[i].Ts < fromUtc) i++;
        return i == 0 ? all : all[i..];
    }

    public void Clear()
    {
        lock (_lock) { Array.Clear(_ring); _head = _count = 0; Volatile.Write(ref _latest, null); }
    }
}
