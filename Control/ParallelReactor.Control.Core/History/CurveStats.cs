using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ParallelReactor.Control.Core.Chart;

namespace ParallelReactor.Control.Core.History;

/// <summary>曲线页的时间范围（样稿 CV.range：5 min / 15 min / 1 h / 全程；自定义在日期选择做好之前只提示）。</summary>
public enum CurveRange { M5, M15, H1, Run }

/// <summary>一个量在区间里的统计：最低 / 最高 / 平均（缺失的点不算）、第一个 / 最后一个有效值、有效点数。</summary>
public readonly record struct CurveStat(double Min, double Max, double Mean, double First, double Last, int Count)
{
    /// <summary>区间增加（最后 − 第一个；吸气量的「区间增加」）。</summary>
    public double Rise => Last - First;
}

/// <summary>一张图的纵轴（样稿 PAGES.curve.after：lo / hi / step）。</summary>
public readonly record struct CurveAxis(double Lo, double Hi, double Step);

/// <summary>
/// 曲线页（融合版 F5，样稿 PAGES.curve）的纯计算：时间窗、纵轴刻度、横轴刻度间隔、区间统计、光标处的读数、分析条（平均反应速率 / 气体消耗 RSD / 温度最大偏差）、导出 CSV。
/// 数据 = 本机的历史缓存（<see cref="ChannelHistory"/>，10 s 一格，只留最近 <see cref="ChannelHistory.KeepMinutes"/> 分钟）。
/// </summary>
public static class CurveStats
{
    /// <summary>一个时间范围的分钟数（全程 = null：从本次运行开始）。</summary>
    public static double? Minutes(CurveRange r) => r switch { CurveRange.M5 => 5, CurveRange.M15 => 15, CurveRange.H1 => 60, _ => null };

    /// <summary>
    /// 时间窗 [A, B]：B = 右端（跟随时 = 现在，暂停时 = 暂停的时刻）；全程从 <paramref name="runStart"/>（本次运行的开始 = 缓存里最早的采样，取整到分钟）开始，
    /// 但不早于 B − 240 min（本机只留这么多；<c>Clipped</c> = 真被截掉了，界面写明）；没有数据时 B − 60 min。窗口至少 1 min。
    /// </summary>
    public static (DateTime A, DateTime B, bool Clipped) Window(CurveRange r, DateTime end, DateTime? runStart)
    {
        if (Minutes(r) is double m) return (end.AddMinutes(-m), end, false);
        var keep = end.AddMinutes(-ChannelHistory.KeepMinutes);
        var a = runStart is { } s ? new DateTime(s.Year, s.Month, s.Day, s.Hour, s.Minute, 0, s.Kind) : end.AddMinutes(-60);
        bool clipped = a < keep;
        if (clipped) a = keep;
        if (end - a < TimeSpan.FromMinutes(1)) a = end.AddMinutes(-1);
        return (a, end, clipped);
    }

    /// <summary>横轴刻度间隔（分钟，样稿 tStep：≤ 15 min 每 5、≤ 60 每 10、其余每 15；超过 3 h 每 30）。</summary>
    public static int TickMinutes(double spanMinutes) => spanMinutes <= 15 ? 5 : spanMinutes <= 60 ? 10 : spanMinutes <= 180 ? 15 : 30;

    /// <summary>
    /// 纵轴（样稿 after）：温度 lo = min(20, 数据)、hi = max(50, 数据)，间隔 25（跨度 &gt; 120 为 50，&gt; 300 为 100）；
    /// 压力 lo = 0、hi = max(1, 数据, 报警上限)，间隔 0.5（&gt; 3 为 1，&gt; 6 为 2）；吸气量 lo = 0、hi = max(1, 数据)，间隔 5（&gt; 40 为 10，&gt; 100 为 nice(hi / 5)）。
    /// lo 往下取整到间隔，hi × 1.04 往上取整到间隔。
    /// 压力不是 MPa 时（<paramref name="pScale"/> = 1 MPa 等于几个显示单位：bar 10、psi 145.04；值与上限已经是显示单位）：hi = max(1 MPa 换算, 数据, 上限)，
    /// 间隔 = nice(hi / 5)（psi 50 / 100 / 200、bar 2 / 5 / 10），刻度落在整数上，矮的图（1366 × 768）里不超过 5 个刻度字。
    /// </summary>
    public static CurveAxis Axis(HistoryField f, IEnumerable<double> values, double? limit = null, double pScale = 1)
    {
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        foreach (var v in values) { if (!double.IsFinite(v)) continue; mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
        if (limit is double l && double.IsFinite(l)) mx = Math.Max(mx, l);
        double lo, hi, step;
        if (f == HistoryField.TempC)
        {
            lo = double.IsFinite(mn) ? Math.Min(20, mn) : 20;
            hi = double.IsFinite(mx) ? Math.Max(50, mx) : 50;
            step = hi - lo > 300 ? 100 : hi - lo > 120 ? 50 : 25;
        }
        else if (f == HistoryField.PressureMPa && pScale != 1)
        {
            lo = 0;
            hi = double.IsFinite(mx) ? Math.Max(pScale, mx) : pScale;
            step = Nice(hi / 5);
        }
        else if (f == HistoryField.PressureMPa)
        {
            lo = 0;
            hi = double.IsFinite(mx) ? Math.Max(1, mx) : 1;
            step = hi > 6 ? 2 : hi > 3 ? 1 : .5;
        }
        else
        {
            lo = 0;
            hi = double.IsFinite(mx) ? Math.Max(1, mx) : 1;
            step = hi > 100 ? Nice(hi / 5) : hi > 40 ? 10 : 5;
        }
        lo = Math.Floor(lo / step) * step;
        hi = Math.Ceiling(hi * 1.04 / step - 1e-9) * step;
        if (hi <= lo) hi = lo + step;
        return new CurveAxis(lo, hi, step);
    }

    /// <summary>nice 步长（1 / 2 / 2.5 / 5 / 10 × 10^k）。</summary>
    public static double Nice(double v)
    {
        if (!(v > 0)) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v))), f = v / e;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * e;
    }

    /// <summary>
    /// 把 [<paramref name="a"/>, <paramref name="b"/>]（本地时刻）里的点写进 <paramref name="buffer"/>（不够大时换一个大的），返回点数；
    /// 缺失 / 断开标记 = NaN（曲线在那里断开）。暂停时窗口右端早于最新的采样，所以不能用 <see cref="ChannelHistory.CopyPoints"/>（那个总是到最新）。
    /// </summary>
    public static int CopyRange(ChannelHistory h, HistoryField f, DateTime a, DateTime b, ref ChartPoint[] buffer)
    {
        int i0 = h.LowerBound(a), i1 = h.LowerBound(b.AddTicks(1));
        int n = Math.Max(0, i1 - i0);
        if (buffer is null || buffer.Length < n) buffer = new ChartPoint[Math.Max(n, 16)];
        for (int i = 0; i < n; i++)
        {
            var s = h[i0 + i];
            buffer[i] = new ChartPoint(ChartTime.Ms(s.T), s.Get(f) ?? double.NaN);
        }
        return n;
    }

    /// <summary>区间 [a, b] 的统计；没有有效点为 null。</summary>
    public static CurveStat? Stat(ChannelHistory? h, HistoryField f, DateTime a, DateTime b)
    {
        if (h is null || h.Count == 0) return null;
        int i0 = h.LowerBound(a), i1 = h.LowerBound(b.AddTicks(1));
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity, sum = 0, first = double.NaN, last = double.NaN;
        int n = 0;
        for (int i = i0; i < i1; i++)
        {
            if (h[i].Get(f) is not double v || !double.IsFinite(v)) continue;
            if (n == 0) first = v;
            last = v;
            mn = Math.Min(mn, v);
            mx = Math.Max(mx, v);
            sum += v;
            n++;
        }
        return n == 0 ? null : new CurveStat(mn, mx, sum / n, first, last, n);
    }

    /// <summary>
    /// 光标 <paramref name="t"/> 处的读数（样稿 at：同一时刻的点，没有就用区间最后一个）：区间 [a, b] 里离 t 最近的有效点，相隔超过 <paramref name="maxGapS"/> 秒为 null。
    /// </summary>
    public static double? At(ChannelHistory? h, HistoryField f, DateTime t, DateTime a, DateTime b, double maxGapS = 60)
    {
        if (h is null || h.Count == 0) return null;
        if (t < a) t = a;
        if (t > b) t = b;
        int i0 = h.LowerBound(a), i1 = h.LowerBound(b.AddTicks(1));
        if (i1 <= i0) return null;
        int j = h.LowerBound(t);
        double? best = null;
        double bd = double.MaxValue;
        var tu = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
        // 往两边各找最近的有效点
        for (int i = Math.Min(j - 1, i1 - 1); i >= i0; i--)
        {
            if (h[i].Get(f) is double v && double.IsFinite(v)) { double d = Math.Abs((h[i].T - tu).TotalSeconds); if (d < bd) { bd = d; best = v; } break; }
        }
        for (int i = Math.Max(j, i0); i < i1; i++)
        {
            if (h[i].Get(f) is double v && double.IsFinite(v)) { double d = Math.Abs((h[i].T - tu).TotalSeconds); if (d < bd) { bd = d; best = v; } break; }
        }
        return bd <= maxGapS ? best : null;
    }

    /// <summary>相对标准偏差 %（样本标准差 / 平均 × 100；少于 2 个或平均为 0 时为 null）。样稿「气体消耗 RSD」。</summary>
    public static double? Rsd(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).ToArray();
        if (v.Length < 2) return null;
        double mean = v.Average();
        if (mean == 0) return null;
        double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1));
        return sd / mean * 100;
    }

    /// <summary>一路导出的列：通道号与它的历史缓存。</summary>
    public readonly record struct CsvChannel(int N, ChannelHistory? History);

    /// <summary>
    /// 导出 CSV（选中各路 [a, b] 里的采样，10 s 一行）：时间, RVn 温度 °C, RVn 压力 MPa, RVn 吸气 mmol, …（只导出勾选的量）。同一格的采样按时刻对齐，缺的格子留空。
    /// </summary>
    public static string Csv(IReadOnlyList<CsvChannel> channels, IReadOnlyList<HistoryField> fields, DateTime a, DateTime b)
    {
        static string Name(HistoryField f) => f switch { HistoryField.TempC => "温度 °C", HistoryField.PressureMPa => "压力 MPa", HistoryField.Mmol => "吸气 mmol", _ => f.ToString() };
        static string Fmt(HistoryField f, double v) => v.ToString(f == HistoryField.PressureMPa ? "0.000" : "0.0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder("时间");
        foreach (var c in channels)
            foreach (var f in fields) sb.Append(",RV").Append(c.N.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Name(f));
        sb.Append('\n');
        // 行 = 各路采样时刻的并集（按 10 s 一格对齐）
        var rows = new SortedDictionary<long, Dictionary<int, HistorySample>>();
        foreach (var c in channels)
        {
            if (c.History is not { Count: > 0 } h) continue;
            int i0 = h.LowerBound(a), i1 = h.LowerBound(b.AddTicks(1));
            for (int i = i0; i < i1; i++)
            {
                var s = h[i];
                if (s.Gap) continue;
                long cell = (s.T.Ticks - DateTime.UnixEpoch.Ticks) / (ChannelHistory.CellSeconds * TimeSpan.TicksPerSecond);
                if (!rows.TryGetValue(cell, out var d)) rows[cell] = d = new Dictionary<int, HistorySample>();
                d[c.N] = s;
            }
        }
        foreach (var (cell, d) in rows)
        {
            var t = new DateTime(DateTime.UnixEpoch.Ticks + cell * ChannelHistory.CellSeconds * TimeSpan.TicksPerSecond, DateTimeKind.Utc).ToLocalTime();
            sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var c in channels)
                foreach (var f in fields)
                {
                    sb.Append(',');
                    if (d.TryGetValue(c.N, out var s) && s.Get(f) is double v && double.IsFinite(v)) sb.Append(Fmt(f, v));
                }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
