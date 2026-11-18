using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Control.Core.Records;

/// <summary>
/// 记录页的 8 路对比（融合版 F7，样稿 rcData / rcStats / rcChart 的算法；纯函数）。每路的结果都在 PC 上按数据算：
/// <list type="bullet">
/// <item>吸气终值 = 最后一个吸气量读数（进行中的会话 = 设备此刻的吸气量，HMI 计算的 gasConsumed）。</item>
/// <item>最大吸气速率 = 2 min 窗口（至少跨 2 个采样）里吸气量增量 ÷ 时间的最大值，mmol/min。</item>
/// <item>到 90 % 用时 = 吸气量第一次到终值 90 % 的时刻（距会话开始，min）；还在反应（终值没定）的写「未到」。</item>
/// <item>保压类（泄漏测试 / 气密预检）：平衡后第 10–40 min 的压降 psi/h（最小二乘斜率），≤ 判据通过。判据 = 设备设置的泄漏率阈值
///   （leakTest.rateLimitPsiPerHr，融合版 F8 起同性能测试页的泄漏测试；不知道时 HMI 默认 2 psi/h）。</item>
/// <item>统计：计入的路（进行中的会话：有读数、并且吸过气或在跑 / 报警暂停；历史会话：吸过气）的平均、样本标准差、RSD；没运行的单列出来。</item>
/// </list>
/// </summary>
public static class RecCompute
{
    /// <summary>保压类判据的默认值：HMI 设置「泄漏率阈值」的出厂值 2 psi/h（Models/AppSettings.LeakRate；样稿 KPI「判据 ≤ 2.0 psi/h」）。设备给了时用设备的。</summary>
    public const double HoldLimitPsiH = 2.0;
    /// <summary>保压类的判读窗口（距会话开始，min）：平衡 10 min 之后到 40 min（样稿「第 10–40 min 压降」）。</summary>
    public const double HoldFromMin = 10, HoldToMin = 40;
    /// <summary>保压类曲线从这里画起（样稿 rcChart：m0 = 8，只画平衡之后）。</summary>
    public const double HoldChartFromMin = 8;
    /// <summary>最大速率的时间窗（min）。</summary>
    public const double RateWindowMin = 2;
    /// <summary>RSD 超过这个就提醒「各路差异明显」（样稿 kpi.warn）。</summary>
    public const double RsdWarnPct = 10;
    public const double PsiPerMPa = 145.037738;
    public const double KPaPerPsi = 6.894757;

    /// <summary>
    /// 一次会话的对比：按会话的通道顺序每路一行（没有数据的通道也给一行，不计入统计）。<paramref name="holdLimit"/> = 保压类判据 psi/h
    /// （设备设置的泄漏率阈值；≤ 0 / NaN 时用 <see cref="HoldLimitPsiH"/>）。
    /// </summary>
    public static RecCompare Compare(RecSession s, RecSeriesSet set, double holdLimit = HoldLimitPsiH)
    {
        if (!(holdLimit > 0) || double.IsInfinity(holdLimit)) holdLimit = HoldLimitPsiH;
        var byN = new Dictionary<int, RecChannelSeries>();
        foreach (var c in set.Channels) byN[c.N] = c;
        var order = s.Channels.Count > 0 ? s.Channels : set.Channels.Select(c => c.N).ToList();
        var rows = new List<RecRow>(order.Count);
        bool hold = s.Hold != RecHold.None;
        foreach (int n in order)
        {
            byN.TryGetValue(n, out var ch);
            var pts = ch?.Points ?? Array.Empty<RecPoint>();
            rows.Add(hold ? HoldRow(n, pts, holdLimit) : UptakeRow(s, set.Live, n, pts, ch?.Now));
        }
        var stats = hold ? RecStats.Empty : Stats(rows);
        var skipped = rows.Where(r => !r.Used).Select(r => r.N).ToList();
        double? testMPa = hold ? TestPressure(rows) : null;
        return new RecCompare(s.Hold, rows, set.Span, set.Live, stats, skipped, testMPa) { HoldLimit = holdLimit };
    }

    private static RecRow UptakeRow(RecSession s, bool liveSession, int n, IReadOnlyList<RecPoint> pts, RecLiveState? now)
    {
        double last = LastG(pts);
        if (liveSession && now != null)
        {
            double g = now.Gas ?? (double.IsNaN(last) ? 0 : last);
            bool used = now.HasReading && (g > 0 || now.Running || now.Alarm);
            bool reacting = now.Running && !now.Cooling || now.Alarm;
            double rate = MaxRate(pts);
            double? t90 = !reacting && g > 0 ? T90(pts, g) : null;
            string res = now.Alarm ? "已暂停" : now.Word;
            return new RecRow(n, pts, used, g, rate, t90, reacting, res, now.Alarm ? RecTone.Err : RecTone.Normal);
        }
        {
            double g = double.IsNaN(last) ? 0 : last;
            bool used = g > 0;
            bool warn = s.Warn is { } w && ContainsRv(w, n);
            bool interrupted = s.State == RecState.Interrupted;
            string res = !used ? (pts.Count == 0 ? "无数据" : "未运行") : interrupted ? "中断" : warn ? "有提醒" : "完成";
            var tone = used && (interrupted || warn) ? RecTone.Warn : RecTone.Normal;
            return new RecRow(n, pts, used, g, used ? MaxRate(pts) : 0, used ? T90(pts, g) : null, false, res, tone);
        }
    }

    private static RecRow HoldRow(int n, IReadOnlyList<RecPoint> pts, double limit)
    {
        double drop = HoldDrop(pts);
        bool has = !double.IsNaN(drop);
        bool ok = has && drop <= limit + 1e-9;
        return new RecRow(n, pts, has, 0, 0, null, false, !has ? "无数据" : ok ? "通过" : "不通过", has && !ok ? RecTone.Err : RecTone.Normal,
            has ? drop : 0, ok);
    }

    /// <summary>「RV3 升温超时」里有 RV3（不把 RV1 当成 RV12 的前缀）。</summary>
    public static bool ContainsRv(string text, int n)
    {
        string key = "RV" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.Ordinal)) >= 0)
        {
            int e = i + key.Length;
            if (e >= text.Length || !char.IsDigit(text[e])) return true;
            i = e;
        }
        return false;
    }

    /// <summary>最后一个有效的吸气量（没有 = NaN）。</summary>
    public static double LastG(IReadOnlyList<RecPoint> pts)
    {
        for (int i = pts.Count - 1; i >= 0; i--) if (double.IsFinite(pts[i].G)) return pts[i].G;
        return double.NaN;
    }

    /// <summary>采样间隔的中位数（min；少于 2 个采样 = NaN）。</summary>
    public static double MedianStep(IReadOnlyList<RecPoint> pts)
    {
        int m = Math.Min(pts.Count - 1, 400);
        if (m < 1) return double.NaN;
        var d = new double[m];
        for (int i = 0; i < m; i++) d[i] = pts[i + 1].M - pts[i].M;
        Array.Sort(d);
        return d[m / 2];
    }

    /// <summary>
    /// 最大吸气速率 mmol/min：每个采样与它之前 k 个采样的差 ÷ 时间差，取最大（k = 覆盖 <see cref="RateWindowMin"/> 的采样数，至少 2；
    /// 样稿实时分支 0.5 min 一点 → 4 点 = 2 min，历史分支 2 个采样）。数据不够为 0。
    /// </summary>
    public static double MaxRate(IReadOnlyList<RecPoint> pts)
    {
        double step = MedianStep(pts);
        if (!(step > 0)) return 0;
        int k = Math.Max(2, (int)Math.Ceiling(RateWindowMin / step - 1e-9));
        double best = 0;
        for (int i = k; i < pts.Count; i++)
        {
            var a = pts[i - k]; var b = pts[i];
            double dt = b.M - a.M;
            if (!(dt > 0) || !double.IsFinite(a.G) || !double.IsFinite(b.G)) continue;
            double r = (b.G - a.G) / dt;
            if (r > best) best = r;
        }
        return best;
    }

    /// <summary>到终值 <paramref name="g"/> 的 90 % 用时（min，第一个 G ≥ 0.9 g 的采样）；没到为 null。</summary>
    public static double? T90(IReadOnlyList<RecPoint> pts, double g)
    {
        if (!(g > 0)) return null;
        double th = g * .9;
        foreach (var p in pts) if (double.IsFinite(p.G) && p.G >= th) return p.M;
        return null;
    }

    /// <summary>
    /// 保压类的压降 psi/h：判读窗口（第 <see cref="HoldFromMin"/>–<see cref="HoldToMin"/> min，数据短于 40 min 时到最后一个采样）里压力的最小二乘斜率取负，
    /// MPa/min → psi/h。窗口里少于 3 个点为 NaN。
    /// </summary>
    public static double HoldDrop(IReadOnlyList<RecPoint> pts)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
        foreach (var p in pts)
        {
            if (p.M < HoldFromMin - 1e-9 || p.M > HoldToMin + 1e-9 || !double.IsFinite(p.P)) continue;
            sx += p.M; sy += p.P; sxx += p.M * p.M; sxy += p.M * p.P; n++;
        }
        if (n < 3) return double.NaN;
        double den = n * sxx - sx * sx;
        if (!(Math.Abs(den) > 1e-12)) return double.NaN;
        double slope = (n * sxy - sx * sy) / den; // MPa/min
        return -slope * PsiPerMPa * 60;
    }

    /// <summary>保压类的测试压力 MPa：判读窗口开始时（第一个 ≥ 10 min 的采样）各路压力的平均。</summary>
    public static double? TestPressure(IReadOnlyList<RecRow> rows)
    {
        double sum = 0; int n = 0;
        foreach (var r in rows)
        {
            foreach (var p in r.Points)
                if (p.M >= HoldFromMin - 1e-9 && double.IsFinite(p.P)) { sum += p.P; n++; break; }
        }
        return n > 0 ? sum / n : null;
    }

    /// <summary>统计（样稿 rcStats）：计入的路的吸气量平均、样本标准差（n − 1）、RSD %。</summary>
    public static RecStats Stats(IReadOnlyList<RecRow> rows)
    {
        var v = rows.Where(r => r.Used).Select(r => r.G).ToList();
        int n = v.Count;
        // 按顺序逐个累加（同样稿 reduce；Enumerable.Sum 会向量化、换加法顺序，末位不同时 toFixed(1) 会差 0.1）
        double sum = 0;
        foreach (var x in v) sum += x;
        double mean = n > 0 ? sum / n : 0;
        double sq = 0;
        foreach (var x in v) sq += (x - mean) * (x - mean);
        double sd = n > 1 ? Math.Sqrt(sq / (n - 1)) : 0;
        return new RecStats(n, mean, sd, mean != 0 ? sd / mean * 100 : 0);
    }

    /// <summary>与平均相比 %（样稿 relCell：(g − mean) / mean × 100）。</summary>
    public static double Rel(double g, double mean) => mean != 0 ? (g - mean) / mean * 100 : 0;

    /// <summary>
    /// 平均线（样稿 rcChart 的 meanLine：吸气量模式、计入的路多于 1 路时画）：以采样最多的一路的时刻为网格，各路按时间线性插值后取平均
    /// （同一时间网格的数据与样稿逐点相同）。某一时刻有路没有数据就跳过这一点。
    /// </summary>
    public static IReadOnlyList<(double M, double V)> MeanLine(IReadOnlyList<RecRow> rows, Func<RecPoint, double> pick)
    {
        var used = rows.Where(r => r.Used && r.Points.Count > 0).ToList();
        if (used.Count < 2) return Array.Empty<(double, double)>();
        var grid = used.OrderByDescending(r => r.Points.Count).First().Points;
        var output = new List<(double, double)>(grid.Count);
        var idx = new int[used.Count];
        foreach (var g in grid)
        {
            double sum = 0; bool ok = true;
            for (int j = 0; j < used.Count && ok; j++)
            {
                double v = Interp(used[j].Points, g.M, ref idx[j], pick);
                if (double.IsNaN(v)) ok = false; else sum += v;
            }
            if (ok) output.Add((g.M, sum / used.Count));
        }
        return output;
    }

    /// <summary>在 <paramref name="pts"/> 里按时间线性插值（<paramref name="cursor"/> 单调前进；范围外 = NaN）。</summary>
    public static double Interp(IReadOnlyList<RecPoint> pts, double m, ref int cursor, Func<RecPoint, double> pick)
    {
        if (pts.Count == 0 || m < pts[0].M - 1e-9 || m > pts[^1].M + 1e-9) return double.NaN;
        while (cursor < pts.Count - 2 && pts[cursor + 1].M < m) cursor++;
        var a = pts[cursor];
        if (cursor + 1 >= pts.Count || Math.Abs(a.M - m) < 1e-9) return pick(a);
        var b = pts[cursor + 1];
        if (Math.Abs(b.M - m) < 1e-9) return pick(b);
        double w = (m - a.M) / (b.M - a.M);
        double va = pick(a), vb = pick(b);
        return va + (vb - va) * w;
    }

    /// <summary>某时刻的读数（样稿悬停提示 at(c)：这一时刻之前最后一个采样）；没有 = NaN。</summary>
    public static double At(IReadOnlyList<RecPoint> pts, double m, Func<RecPoint, double> pick)
    {
        if (pts.Count == 0) return double.NaN;
        var best = pts[0];
        foreach (var p in pts) { if (p.M > m) break; best = p; }
        return pick(best);
    }
}
