using System;
using System.Collections;
using System.Collections.Generic;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.History;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 一段曲线点（复用的缓冲 + 有效长度）：页面每秒从 Core 的 <see cref="ChannelHistory"/>（10 s 一格的环形缓冲，增量追加）把窗口里的点拷进同一个缓冲
/// （<see cref="ChannelHistory.CopyPoints"/>，不分配），交给 ChartOptions 的系列 —— 不再像旧界面那样每帧从 7200 帧重建、也不每秒新建数组（R10）。
/// 曲线（TimeSeriesChart）在拿到新的 ChartOptions 时立刻按它重排，所以下一次刷新覆盖缓冲时旧的版面已经不用了。
/// </summary>
public sealed class PointWindow : IReadOnlyList<ChartPoint>
{
    private ChartPoint[] _buf = Array.Empty<ChartPoint>();
    private int _count;

    public ChartPoint this[int index] => (uint)index < (uint)_count ? _buf[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public int Count => _count;

    /// <summary>
    /// 换成 <paramref name="h"/> 第 <paramref name="field"/> 项最近 <paramref name="minutes"/> 分钟的点（断开标记 = NaN，曲线在那里断开）；
    /// <paramref name="h"/> 为 null（还没有这一路的数据）时为空。
    /// </summary>
    public PointWindow Fill(ChannelHistory? h, HistoryField field, DateTime now, double minutes)
    {
        _count = h is null ? 0 : h.CopyPoints(field, now, minutes, ref _buf);
        return this;
    }

    /// <summary>清空。</summary>
    public PointWindow Clear()
    {
        _count = 0;
        return this;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<ChartPoint> IEnumerable<ChartPoint>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<ChartPoint>
    {
        private readonly PointWindow _w;
        private int _i;

        public Enumerator(PointWindow w)
        {
            _w = w;
            _i = -1;
        }

        public ChartPoint Current => _w._buf[_i];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_i < _w._count;
        public void Reset() => _i = -1;
        public void Dispose() { }
    }
}
