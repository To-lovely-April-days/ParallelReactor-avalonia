using System;
using System.Collections.Generic;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.History;

/// <summary>曲线数据的量（样稿 series 的 key）。顺序与 App 的 HistoryKey 相同（可以直接按整数换）。</summary>
public enum HistoryField { TempC, PressureMPa, HeaterPct, Rpm, Mmol, SpC, PTargetMPa }

/// <summary>
/// 一个采样（样稿 Sample：每 10 s 一个）：时刻（UTC）+ 温度、压力（MPa）、加热输出 %、转速、耗气 mmol、温度设定值（加热 / 冷却时才有）、目标压力。
/// 缺失为 null；<see cref="Gap"/> = 断开标记（全部为空，曲线在这里断开：断线、设备时钟跳变）。
/// </summary>
public readonly record struct HistorySample(DateTime T, double? TempC, double? PressureMPa, double? HeaterPct, double? Rpm, double? Mmol, double? SpC, double? PTargetMPa, bool Gap = false)
{
    public double? Get(HistoryField f) => f switch
    {
        HistoryField.TempC => TempC,
        HistoryField.PressureMPa => PressureMPa,
        HistoryField.HeaterPct => HeaterPct,
        HistoryField.Rpm => Rpm,
        HistoryField.Mmol => Mmol,
        HistoryField.SpC => SpC,
        HistoryField.PTargetMPa => PTargetMPa,
        _ => null,
    };
}

/// <summary>
/// 一个反应釜的曲线数据（PORT-PLAN §3.2 第 11 条 / §5 M5「数据」：取代旧界面每帧从 7200 帧重建曲线）：按时间排好的环形缓冲，增量追加。
/// <list type="bullet">
/// <item><b>10 s 一格</b>：同一格（按 UTC 的 10 s 整数倍对齐）里的帧只留最后一帧（时刻也换成它的）—— 曲线末端始终是最新读数，点距约 10 s，
///   60 min 窗口约 360 个点。演示数据本来就是每 10 s 一个采样，原样进来。</item>
/// <item><b>保留</b> <see cref="KeepMinutes"/>（240 min，同样稿 KEEP_MIN）：追加时去掉更早的；容量另有上限（<see cref="Capacity"/>）。</item>
/// <item><b>断开</b>：两帧相隔超过 <see cref="GapSeconds"/>（默认 30 s：断线、HMI 停发）时先插一个断开标记（全空），曲线在这里断开而不是画一条直线连过去；
///   断线时界面也可以主动调 <see cref="MarkGap"/>。读数为 null（传感器离线）同样断开。</item>
/// <item><b>时钟回跳</b>（设备时间被往回调）：丢掉新帧时刻之后的旧点再追加（缓冲始终按时间排序，二分查找才成立）。</item>
/// </list>
/// 窗口查询：<see cref="LowerBound"/> 二分找起点；<see cref="Series"/>（IChannelHistory 的形状，本地时刻）/ <see cref="Points"/>（曲线点）/ <see cref="CopyPoints"/>（复用缓冲，不分配）。
/// 不是线程安全的：在 UI 线程上用。<see cref="Version"/> 每次改动加一（曲线只在它变了时重算版面）。
/// </summary>
public sealed class ChannelHistory
{
    /// <summary>一格的秒数（样稿 SAMPLE_S）。</summary>
    public const int CellSeconds = 10;
    /// <summary>保留的分钟数（样稿 KEEP_MIN = 4 h）。</summary>
    public const int KeepMinutes = 240;
    /// <summary>默认容量：4 h × 每分钟 6 格 + 断开标记的余量。</summary>
    public const int DefaultCapacity = KeepMinutes * 6 + 160;

    private HistorySample[] _buf;
    private int _head, _count;

    public ChannelHistory(int capacity = DefaultCapacity, double gapSeconds = 30)
    {
        _buf = new HistorySample[Math.Max(16, capacity)];
        GapSeconds = gapSeconds > 0 ? gapSeconds : 30;
    }

    /// <summary>两个采样相隔超过这么多秒就先插断开标记。</summary>
    public double GapSeconds { get; }
    public int Capacity => _buf.Length;
    /// <summary>采样数（含断开标记）。</summary>
    public int Count => _count;
    /// <summary>每次改动加一。</summary>
    public int Version { get; private set; }

    /// <summary>第 i 个采样（0 = 最旧）。</summary>
    public HistorySample this[int i]
    {
        get
        {
            if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
            return _buf[(_head + i) % _buf.Length];
        }
    }

    /// <summary>最新的采样；空为 null。</summary>
    public HistorySample? Last => _count > 0 ? this[_count - 1] : null;

    /// <summary>清空（换设备 / HMI 重启）。</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
        Version++;
    }

    /// <summary>
    /// 追加一个采样（实时：每帧；演示：每 10 s）。<paramref name="s"/>.T 必须是 UTC 或本地时刻（内部一律换成 UTC）。
    /// 同一格里替换最后一个采样；离上一个超过 <see cref="GapSeconds"/> 先插断开标记；比最后一个早（时钟回跳）先丢掉不早于它的旧点。
    /// </summary>
    public void Append(HistorySample s)
    {
        var t = Utc(s.T);
        s = s with { T = t };
        if (_count > 0)
        {
            var last = this[_count - 1];
            if (t < last.T)
            {
                // 时钟回跳：去掉新帧时刻之后（含同一时刻）的点
                while (_count > 0 && this[_count - 1].T >= t) _count--;
                if (_count > 0) last = this[_count - 1];
            }
            if (_count > 0)
            {
                if (!s.Gap && !last.Gap && Cell(t) == Cell(last.T))
                {
                    _buf[(_head + _count - 1) % _buf.Length] = s;
                    Version++;
                    return;
                }
                if (!s.Gap && !last.Gap && (t - last.T).TotalSeconds > GapSeconds)
                    Push(new HistorySample(last.T + TimeSpan.FromTicks((t - last.T).Ticks / 2), null, null, null, null, null, null, null, Gap: true));
            }
        }
        if (s.Gap && _count > 0 && this[_count - 1].Gap) { Version++; return; }   // 连续的断开标记只留一个
        Push(s);
        // 只留最近 4 h —— 按读数的时刻；断开标记不裁（它的时刻是本机估的「现在」，不是设备的数据：复核，以前时钟差不对时一个标记就把 4 h 的曲线全裁掉了）
        if (!s.Gap)
        {
            var cut = t - TimeSpan.FromMinutes(KeepMinutes);
            while (_count > 0 && this[0].T < cut) { _head = (_head + 1) % _buf.Length; _count--; }
        }
        Version++;
    }

    /// <summary>追加一帧的读数（null = 没有读数）。</summary>
    public void Append(DateTime t, double? tempC, double? pressureMPa, double? heaterPct = null, double? rpm = null, double? mmol = null, double? spC = null, double? pTargetMPa = null) =>
        Append(new HistorySample(t, tempC, pressureMPa, heaterPct, rpm, mmol, spC, pTargetMPa));

    /// <summary>
    /// 主动断开（断线、重连、换程序时不想把两段连起来）：在 <paramref name="t"/> 处插一个断开标记（最后一个已经是断开标记时不重复）。
    /// 标记的时刻夹在 [最后一个采样, 最后一个采样 + <see cref="GapSeconds"/>] 里：<paramref name="t"/> 是本机时钟换算的「现在」，时钟差不对时可能差几个小时 ——
    /// 远在将来的标记会让下一帧走「时钟回跳」把它删掉（复核）；曲线只要在这里断开，标记离数据近一点就够了。
    /// </summary>
    public void MarkGap(DateTime t)
    {
        if (_count == 0) return;
        var u = Utc(t);
        var last = this[_count - 1].T;
        if (u < last) u = last;
        var latest = last + TimeSpan.FromSeconds(GapSeconds);
        if (u > latest) u = latest;
        Append(new HistorySample(u, null, null, null, null, null, null, null, Gap: true));
    }

    /// <summary>第一个时刻 ≥ <paramref name="t"/> 的采样下标（二分）；都更早时 = <see cref="Count"/>。</summary>
    public int LowerBound(DateTime t)
    {
        var u = Utc(t);
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (this[mid].T < u) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>离 <paramref name="t"/> 最近的采样下标（两边一样近取前一个）；空为 −1。</summary>
    public int Nearest(DateTime t)
    {
        if (_count == 0) return -1;
        int i = LowerBound(t);
        if (i <= 0) return 0;
        if (i >= _count) return _count - 1;
        var u = Utc(t);
        return (u - this[i - 1].T) <= (this[i].T - u) ? i - 1 : i;
    }

    /// <summary>
    /// 最近 <paramref name="minutes"/> 分钟（时刻 ≥ now − minutes）的 (时刻（本地）, 值)，最旧在前，缺失为 null（同样稿 series、App 的 IChannelHistory）。
    /// 断开标记也在里面（值为 null），曲线在那里断开。<paramref name="minutes"/> ≤ 0 按 60。
    /// </summary>
    public IReadOnlyList<(DateTime T, double? V)> Series(HistoryField field, DateTime now, double minutes = 60)
    {
        int i0 = LowerBound(WindowStart(now, minutes));
        var list = new (DateTime, double?)[_count - i0];
        for (int i = i0; i < _count; i++)
        {
            var s = this[i];
            list[i - i0] = (s.T.ToLocalTime(), s.Get(field));
        }
        return list;
    }

    /// <summary>同 <see cref="Series"/>，给曲线用的点（一份新数组）。</summary>
    public IReadOnlyList<ChartPoint> Points(HistoryField field, DateTime now, double minutes = 60)
    {
        ChartPoint[] buf = Array.Empty<ChartPoint>();
        int n = CopyPoints(field, now, minutes, ref buf);
        if (n == buf.Length) return buf;
        var a = new ChartPoint[n];
        Array.Copy(buf, a, n);
        return a;
    }

    /// <summary>把窗口里的点写进 <paramref name="buffer"/>（不够大时换一个大的），返回点数 —— 曲线每秒刷新时复用同一个缓冲，不分配（R10）。</summary>
    public int CopyPoints(HistoryField field, DateTime now, double minutes, ref ChartPoint[] buffer)
    {
        int i0 = LowerBound(WindowStart(now, minutes));
        int n = _count - i0;
        if (buffer is null || buffer.Length < n) buffer = new ChartPoint[n];
        for (int i = 0; i < n; i++)
        {
            var s = this[i0 + i];
            buffer[i] = new ChartPoint(ChartTime.Ms(s.T), s.Get(field) ?? double.NaN);
        }
        return n;
    }

    private static DateTime WindowStart(DateTime now, double minutes) =>
        Utc(now) - TimeSpan.FromTicks((long)Math.Round((minutes > 0 ? minutes : 60) * 60000 * TimeSpan.TicksPerMillisecond));

    private void Push(HistorySample s)
    {
        if (_count == _buf.Length)
        {
            // 满了：盖掉最旧的
            _buf[_head] = s;
            _head = (_head + 1) % _buf.Length;
            return;
        }
        _buf[(_head + _count) % _buf.Length] = s;
        _count++;
    }

    private static long Cell(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / (CellSeconds * TimeSpan.TicksPerSecond);

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
}

/// <summary>
/// 一台设备全部反应釜的曲线数据（每路一个 <see cref="ChannelHistory"/>，按需建）。
/// 实时：每收到一帧 <see cref="AppendFrame"/>（psi → MPa；温度设定值只在加热时记；转速 = 共用搅拌的设定值，关着为 0）；断线时 <see cref="MarkGap"/>；HMI 重启（bootId 变）时 <see cref="Clear"/>。
/// 演示：<see cref="SyncDemo"/> 把 DemoChannel.History 里新增的采样追加进来（结果与 DemoDevice.Series 逐点相同）。
/// </summary>
public sealed class DeviceHistory
{
    private readonly Dictionary<int, ChannelHistory> _channels = new();
    private readonly int _capacity;
    private readonly double _gap;

    public DeviceHistory(int capacity = ChannelHistory.DefaultCapacity, double gapSeconds = 30)
    {
        _capacity = capacity;
        _gap = gapSeconds;
    }

    /// <summary>任何一路有改动就加一。</summary>
    public int Version { get; private set; }

    /// <summary>第 n 路（没有就建一个空的）。</summary>
    public ChannelHistory Channel(int n)
    {
        if (!_channels.TryGetValue(n, out var h)) _channels[n] = h = new ChannelHistory(_capacity, _gap);
        return h;
    }

    /// <summary>有这一路的数据。</summary>
    public bool Has(int n) => _channels.TryGetValue(n, out var h) && h.Count > 0;

    /// <summary>追加一帧遥测（时刻 = 帧的 Ts，UTC 设备时钟）：每个已安装的通道一个采样。</summary>
    public void AppendFrame(TelemetrySnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Channels is null) return;
        double rpm = frame.Stir is { } st ? (st.On ? st.RpmSp : 0) : double.NaN;
        foreach (var ch in frame.Channels)
        {
            if (ch is null || !ch.Installed) continue;
            Channel(ch.Id).Append(SampleOf(frame.Ts, ch, double.IsNaN(rpm) ? null : rpm));
        }
        Version++;
    }

    /// <summary>
    /// 一个通道的采样（样稿 sampleOf 的真机写法）：温度 / 加热输出原样；压力 psi → MPa；温度设定值只在加热时（StateMap.IsHeating）记；
    /// 目标压力 = PressureSpPsi &gt; 0 时；耗气 = GasConsumed（HMI 目前恒为 null）。
    /// </summary>
    public static HistorySample SampleOf(DateTime ts, ChannelSnap ch, double? rpm)
    {
        ArgumentNullException.ThrowIfNull(ch);
        return new HistorySample(ts, ch.TempC, Units.PsiToMpa(ch.PressurePsi), ch.HeatOutputPct, rpm, ch.GasConsumed,
            StateMap.IsHeating(ch) ? ch.TempSpC : null, ch.PressureSpPsi > 0 ? Units.PsiToMpa(ch.PressureSpPsi) : null);
    }

    /// <summary>在所有已有的通道上插断开标记（断线时）。</summary>
    public void MarkGap(DateTime t)
    {
        foreach (var h in _channels.Values) h.MarkGap(t);
        Version++;
    }

    /// <summary>清空所有通道（HMI 重启、换设备）。</summary>
    public void Clear()
    {
        foreach (var h in _channels.Values) h.Clear();
        Version++;
    }

    /// <summary>
    /// 演示数据：把 DemoChannel.History 里比已有的更新的采样追加进来（第一次是 60 min 的预生成历史；之后每 10 s 一个）。
    /// 演示的历史整个重建过（DemoFleet 重置 / 改参数：第一个采样比已有的晚不了、或最后一个比已有的早）时先清空再全部追加。
    /// </summary>
    public void SyncDemo(DemoDevice d)
    {
        ArgumentNullException.ThrowIfNull(d);
        foreach (var ch in d.Channels)
        {
            if (!ch.Installed) continue;
            var h = Channel(ch.N);
            var src = ch.History;
            if (src.Count == 0) { if (h.Count > 0) h.Clear(); continue; }
            int i = 0;
            if (h.Count > 0)
            {
                // 正常情况：演示历史只在末尾追加（开头按 4 h 裁掉），已有的最后一个采样在演示历史里原样还在 —— 从它后面接着追加。
                // 否则（重置 / 改参数重建过：找不到它、值变了，或开头多出对不上的旧点）清空重来。
                var lastT = h[h.Count - 1].T;
                int j = LowerIndex(src, lastT);
                bool ok = j < src.Count && Utc(src[j].T) == lastT && Same(src[j], h[h.Count - 1]);
                if (ok)
                {
                    int j0 = LowerIndex(src, h[0].T);
                    ok = j0 < src.Count && (Utc(src[j0].T) == h[0].T ? Same(src[j0], h[0]) : Utc(src[0].T) >= h[0].T);
                }
                if (ok) i = j + 1;
                else h.Clear();
            }
            for (; i < src.Count; i++)
            {
                var s = src[i];
                h.Append(new HistorySample(s.T, s.TempC, s.PressureMPa, s.HeaterPct, s.Rpm, s.Mmol, s.SpC, s.PTargetMPa));
            }
        }
        Version++;
    }

    /// <summary>第 n 路最近 <paramref name="minutes"/> 分钟的 (本地时刻, 值)；没有这一路为空表。</summary>
    public IReadOnlyList<(DateTime T, double? V)> Series(int n, HistoryField field, DateTime now, double minutes = 60) =>
        _channels.TryGetValue(n, out var h) ? h.Series(field, now, minutes) : Array.Empty<(DateTime, double?)>();

    /// <summary>第 n 路最近 <paramref name="minutes"/> 分钟的曲线点；没有这一路为空表。</summary>
    public IReadOnlyList<ChartPoint> Points(int n, HistoryField field, DateTime now, double minutes = 60) =>
        _channels.TryGetValue(n, out var h) ? h.Points(field, now, minutes) : Array.Empty<ChartPoint>();

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();

    /// <summary>演示历史里第一个时刻 ≥ <paramref name="utc"/> 的下标（二分）。</summary>
    private static int LowerIndex(IReadOnlyList<DemoSample> src, DateTime utc)
    {
        int lo = 0, hi = src.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Utc(src[mid].T) < utc) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static bool Same(DemoSample a, HistorySample b) =>
        Utc(a.T) == b.T && Eq(a.TempC, b.TempC) && Eq(a.PressureMPa, b.PressureMPa) && Eq(a.HeaterPct, b.HeaterPct) && Eq(a.Rpm, b.Rpm)
        && Eq(a.Mmol, b.Mmol) && Eq(a.SpC, b.SpC) && Eq(a.PTargetMPa, b.PTargetMPa);

    private static bool Eq(double? a, double? b) => a is null ? b is null : b is double v && a.Value.Equals(v);
}
