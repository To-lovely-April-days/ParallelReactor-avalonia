using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Core.Chart;

/// <summary>一条纵轴的结果：输入 + 刻度（<see cref="Lo"/> / <see cref="Hi"/> 是对齐到刻度后的范围，Y 映射按它）+ 实际小数位（输入给了 Dec 用输入的）。</summary>
public sealed record ChartAxisLayout(ChartAxis Axis, double Lo, double Hi, double Step, IReadOnlyList<double> Ticks, int Dec)
{
    public string? Label => Axis.Label;
    public string? Unit => Axis.Unit;
    public string? Color => Axis.Color;
}

/// <summary>纵轴刻度：值、屏幕 y（JS 的数值，未取整）、文字（v.toFixed(dec)）、文字基线 y。</summary>
public readonly record struct ChartTick(double Value, double Y, string Text, double TextY);

/// <summary>横轴刻度：时刻（UTC 毫秒）、屏幕 x、文字（本地 'HH:MM'）。</summary>
public readonly record struct ChartTimeTick(double T, double X, string Text);

/// <summary>
/// 一条参考线的版面：屏幕 y、线色 / 文字色令牌、虚线、文字与位置（<see cref="LabelX"/> / <see cref="LabelBase"/>；<see cref="LabelEnd"/> = 右对齐）。
/// 文字的位置按样稿：线上方右端 → 线下方右端 → 线上方左端 → 线下方左端，取第一个不被曲线穿过、又在坐标框里的（「上限 3.000 MPa」不被温度线划掉）。
/// </summary>
public sealed record ChartRefLayout(ChartRefLine Line, double Y, string Color, string? Label, double LabelX, double LabelBase, bool LabelEnd, string LabelColor);

/// <summary>一条事件标记的版面：屏幕 x、线色 / 文字色令牌、文字与位置（靠右边时文字画在线的左边、右对齐）。</summary>
public sealed record ChartMarkerLayout(ChartMarker Marker, double X, string Color, string? Label, double LabelX, double LabelY, bool LabelEnd, string LabelColor);

/// <summary>悬停提示的一行：系列、颜色令牌、读数文字（按系列 / 轴的小数位）、单位、圆点位置。</summary>
public readonly record struct ChartTipRow(int Series, string Name, string Color, string Value, string? Unit, double DotX, double DotY);

/// <summary>悬停：十字线的 x、对齐到的时刻、时间文字（'10:11:30'）、各行。</summary>
public sealed record ChartHover(double CrossX, double T, string TimeText, IReadOnlyList<ChartTipRow> Rows);

/// <summary>
/// 曲线的版面（样稿 ui.lineChart 的 draw() 一次算出的全部几何）。<see cref="Drawn"/> = false 表示容器太小（&lt; 40 × 40），什么都不画。
/// 坐标单位是设计 px（DIP），原点在控件左上角。界面只按这个结果画：网格 / 刻度 / 坐标框 / 轴标题 / 参考线 / 标记 / 曲线（<see cref="DrawOrder"/>：虚线先画、实线后画）/
/// 末端圆点 / 文字（参考线与标记的文字最后画，带 --ch-bg 描边）/ 空数据文字。
/// </summary>
public sealed class ChartGeometry
{
    internal ChartGeometry() { }

    public bool Drawn { get; internal set; }
    public double Width { get; internal set; }
    public double Height { get; internal set; }
    public double PadL { get; internal set; }
    public double PadR { get; internal set; }
    public double PadT { get; internal set; }
    public double PadB { get; internal set; }
    /// <summary>绘图区宽 / 高。</summary>
    public double Pw { get; internal set; }
    public double Ph { get; internal set; }
    /// <summary>横轴范围（UTC 毫秒）。</summary>
    public double X0 { get; internal set; }
    public double X1 { get; internal set; }
    /// <summary>有数据的系列（至少一个有效点），没写颜色的已按序号填上 --ch-s1/2/3。</summary>
    public IReadOnlyList<ChartSeries> Series { get; internal set; } = Array.Empty<ChartSeries>();
    public IReadOnlyList<ChartAxisLayout> Axes { get; internal set; } = Array.Empty<ChartAxisLayout>();
    /// <summary>两条纵轴。</summary>
    public bool TwoAxes => Axes.Count > 1;
    /// <summary>左轴刻度（每个都有一条水平网格线）。</summary>
    public IReadOnlyList<ChartTick> LeftTicks { get; internal set; } = Array.Empty<ChartTick>();
    /// <summary>右轴刻度（只有文字，超出绘图区 1 px 以上的已去掉）。</summary>
    public IReadOnlyList<ChartTick> RightTicks { get; internal set; } = Array.Empty<ChartTick>();
    /// <summary>横轴刻度步长（毫秒，1 / 2 / 5 / 10 / 15 / 20 / 30 / 60 / 120 min 之一）。</summary>
    public double TimeStep { get; internal set; }
    public IReadOnlyList<ChartTimeTick> TimeTicks { get; internal set; } = Array.Empty<ChartTimeTick>();
    public IReadOnlyList<ChartRefLayout> RefLines { get; internal set; } = Array.Empty<ChartRefLayout>();
    public IReadOnlyList<ChartMarkerLayout> Markers { get; internal set; } = Array.Empty<ChartMarkerLayout>();
    /// <summary>画线的顺序（<see cref="Series"/> 的下标）：虚线（设定值 / 目标）先画，实线（实测值）后画 —— 灰色虚线不会压在同色的实测曲线上面。</summary>
    public IReadOnlyList<int> DrawOrder { get; internal set; } = Array.Empty<int>();
    /// <summary>没有任何有数据的系列时的文字（emptyText，默认「暂无数据」），画在绘图区正中；有数据时为 null。</summary>
    public string? EmptyText { get; internal set; }
    /// <summary>悬停时离系列首尾多远还算（毫秒）。</summary>
    public double TipGapMs { get; internal set; } = 30000;
    internal IReadOnlyList<double[]>? Segs { get; set; }

    /// <summary>时刻（UTC 毫秒）→ 屏幕 x。</summary>
    public double X(double t) => PadL + Pw * (t - X0) / (X1 - X0);

    /// <summary>值 → 屏幕 y（第 <paramref name="axis"/> 条轴）。</summary>
    public double Y(double v, int axis = 0)
    {
        var ax = Axes[axis >= 0 && axis < Axes.Count ? axis : 0];
        double span = ax.Hi - ax.Lo;
        return PadT + Ph * (1 - (v - ax.Lo) / (span != 0 && !double.IsNaN(span) ? span : 1));
    }

    /// <summary>
    /// 第 <paramref name="series"/> 条系列在横轴窗口内的屏幕点，依次写进 <paramref name="xs"/> / <paramref name="ys"/>（缓冲由调用方复用，不够大时换大的）：
    /// 断开处（窗口外 / 缺失值）写一个 NaN 点（JS 的 <c>pen = false</c>），返回写入的点数。连续两个 NaN 不重复写。
    /// </summary>
    public int ScreenPoints(int series, ref double[] xs, ref double[] ys)
    {
        var s = Series[series];
        int ai = s.Axis;
        var pts = s.Points;
        if (xs.Length < pts.Count + 1) xs = new double[pts.Count + 1];
        if (ys.Length < pts.Count + 1) ys = new double[pts.Count + 1];
        int k = 0;
        bool pen = false;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p.T < X0 || p.T > X1 || !p.Ok)
            {
                if (pen) { xs[k] = double.NaN; ys[k] = double.NaN; k++; }
                pen = false;
                continue;
            }
            xs[k] = X(p.T);
            ys[k] = Y(p.V, ai);
            k++;
            pen = true;
        }
        return k;
    }

    /// <summary>
    /// 末端圆点（半径 3，系列色填充）：最后一个点有效、在横轴窗口内、系列没写 NoEnd 时给出屏幕位置，否则 null（同样稿：只看最后一个点，不往前找）。
    /// </summary>
    public (double X, double Y)? EndDot(int series)
    {
        var s = Series[series];
        if (s.NoEnd || s.Points.Count == 0) return null;
        var last = s.Points[s.Points.Count - 1];
        if (!last.Ok || last.T > X1 || last.T < X0) return null;
        return (X(last.T), Y(last.V, s.Axis));
    }

    /// <summary>屏幕 x 对应的时刻（悬停）。</summary>
    public double TimeAt(double x) => X0 + (x - PadL) / Pw * (X1 - X0);

    /// <summary>屏幕点在绘图区内（悬停时才显示十字线和提示）。</summary>
    public bool InPlot(double x, double y) => Drawn && x >= PadL && x <= PadL + Pw && y >= PadT && y <= PadT + Ph;
}

/// <summary>
/// 曲线的版面计算（样稿 js/ui.js ui.lineChart draw() / onMove 的移植，运算顺序与取舍一一对应；ChartTicksTests 把结果按 SVG 的写法拼出来、与样稿在 JSC 里画出的 SVG 逐项比较）。
/// <list type="bullet">
/// <item>上边距 34，下边距 32（有横轴标题时 50）；左右边距 = max(44, 最长刻度文字的字符数 × 8 + 14)，只有一条纵轴时右边距 18。</item>
/// <item>纵轴范围：窗口内的有效点 + 参考线（Fit）→ 固定 Min / Max → 最小跨度（MinSpan，否则按单位）→ 刻度：Min、Max 都固定且没给 MaxTicks 时 5 格，
///   否则按绘图区高度减少（≥ 30 px 一格，默认最多 clamp(round(高 / 48), 2, 6) 格）。</item>
/// <item>横轴刻度：步长取 1 / 2 / 5 / 10 / 15 / 20 / 30 / 60 / 120 min 里第一个使格数 ≤ max(3, floor(宽 / 72)) 的，刻度对齐到步长的整数倍（UTC 毫秒）。</item>
/// </list>
/// 文字宽度（参考线 / 标记文字 14 px 600）由调用方量（<c>labelWidth</c>，界面用同一字体的 FormattedText）；不给时按字符数估。
/// 纯函数，不碰界面；每次数据或尺寸变化算一次，绘制时只读结果（R10）。
/// </summary>
public static class ChartLayout
{
    public const double PadTop = 34, PadBottom = 32, PadBottomWithLabel = 50, MinPad = 44, PadRightOne = 18;
    /// <summary>横轴刻度的候选步长（毫秒）。</summary>
    public static readonly IReadOnlyList<double> TimeSteps = Array.AsReadOnly(new double[] { 1, 2, 5, 10, 15, 20, 30, 60, 120 }.Select(m => m * 60000).ToArray());

    /// <summary>
    /// 算版面。<paramref name="width"/> / <paramref name="height"/> 是容器尺寸（JS 取 floor）；<paramref name="now"/> 只在没有横轴范围、又没有数据时用（JS Date.now()）。
    /// <paramref name="labelWidth"/>：参考线 / 标记文字（14 px、600）的宽度；null 按每个字符 8.4 px（汉字 14 px）估。
    /// </summary>
    public static ChartGeometry Compute(ChartOptions o, double width, double height, Func<string, double>? labelWidth = null, double? now = null)
    {
        ArgumentNullException.ThrowIfNull(o);
        var g = new ChartGeometry();
        double W = Math.Floor(width), H = Math.Floor(height);
        g.Width = W;
        g.Height = H;
        if (W < 40 || H < 40 || double.IsNaN(W) || double.IsNaN(H)) return g;
        g.Drawn = true;
        var measure = labelWidth ?? EstimateWidth;

        // 有数据的系列；没写颜色的按（筛选后的）序号取 --ch-s1/2/3
        var series = new List<ChartSeries>();
        foreach (var s in o.Series)
        {
            if (s?.Points is null) continue;
            bool any = false;
            foreach (var p in s.Points) if (p.Ok) { any = true; break; }
            if (!any) continue;
            series.Add(s.Color is { Length: > 0 } ? s : s with { Color = ChartTokens.Auto[series.Count % ChartTokens.Auto.Count] });
        }
        var axesIn = o.YAxes is { Count: > 0 } ? o.YAxes : new[] { new ChartAxis() };
        var refs = new List<ChartRefLine>();
        foreach (var r in o.RefLines) if (r is not null && double.IsFinite(r.V)) refs.Add(r);

        double x0 = double.NaN, x1 = double.NaN;
        bool hasX0 = o.XMin is double, hasX1 = o.XMax is double;
        if (hasX0) x0 = o.XMin!.Value;
        if (hasX1) x1 = o.XMax!.Value;
        if (!hasX0 || !hasX1)
        {
            double a = double.PositiveInfinity, b = double.NegativeInfinity;
            foreach (var s in series)
            {
                a = Math.Min(a, s.Points[0].T);
                b = Math.Max(b, s.Points[s.Points.Count - 1].T);
            }
            double nowMs = now ?? ChartTime.Ms(DateTime.UtcNow);
            if (!hasX0) x0 = double.IsFinite(a) ? a : nowMs - 3600000;
            if (!hasX1) x1 = double.IsFinite(b) ? b : nowMs;
        }
        if (x1 <= x0) x1 = x0 + 60000;
        g.X0 = x0;
        g.X1 = x1;

        double padT = PadTop, padB = string.IsNullOrEmpty(o.XLabel) ? PadBottom : PadBottomWithLabel;
        double ph0 = H - padT - padB;
        var axes = new List<ChartAxisLayout>(axesIn.Count);
        for (int i = 0; i < axesIn.Count; i++)
        {
            var ax = axesIn[i] ?? new ChartAxis();
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var s in series)
            {
                if (s.Axis != i) continue;
                foreach (var p in s.Points)
                    if (p.Ok && p.T >= x0 && p.T <= x1) { lo = Math.Min(lo, p.V); hi = Math.Max(hi, p.V); }
            }
            foreach (var r in refs)
                if (r.Axis == i && r.Fit) { lo = Math.Min(lo, r.V); hi = Math.Max(hi, r.V); }
            if (ax.Min is double mn) lo = mn;
            if (ax.Max is double mx) hi = mx;
            if (!double.IsFinite(lo) || !double.IsFinite(hi)) { lo = ax.Min ?? 0; hi = ax.Max ?? 1; }
            double span = ax.MinSpan ?? ChartTicks.MinSpanOf(ax.Unit);
            if (span > 0 && hi - lo < span)
            {
                double c = (hi + lo) / 2;
                lo = c - span / 2;
                hi = c + span / 2;
                if (ax.Min is double amin && lo < amin) { lo = amin; hi = lo + span; }
                if (ax.Max is double amax && hi > amax) { hi = amax; lo = Math.Max(ax.Min ?? double.NegativeInfinity, hi - span); }
            }
            int maxTicks = ax.MaxTicks ?? 0;
            bool fixedRange = ax.Min is not null && ax.Max is not null && maxTicks == 0;
            var t = fixedRange ? ChartTicks.Nice(lo, hi, 5)
                : ChartTicks.Fit(lo, hi, ph0, maxTicks != 0 ? maxTicks : (int)Math.Max(2, Math.Min(6, JsNumber.Round(ph0 / 48))));
            axes.Add(new ChartAxisLayout(ax, t.Lo, t.Hi, t.Step, t.Ticks, ax.Dec ?? t.Dec));
        }
        g.Axes = axes.AsReadOnly();
        g.Series = series.AsReadOnly();
        bool two = axes.Count > 1;
        double padL = Math.Max(MinPad, TickWidth(axes[0])), padR = two ? Math.Max(MinPad, TickWidth(axes[1])) : PadRightOne;
        double pw = W - padL - padR, ph = ph0;
        g.PadL = padL; g.PadR = padR; g.PadT = padT; g.PadB = padB; g.Pw = pw; g.Ph = ph;

        // 纵轴刻度（左轴带网格线；右轴只有文字，超出绘图区的去掉）
        var left = new List<ChartTick>(axes[0].Ticks.Count);
        foreach (double v in axes[0].Ticks)
        {
            double y = g.Y(v, 0);
            double yr = Round1(y);
            left.Add(new ChartTick(v, y, JsNumber.ToFixed(v, axes[0].Dec), yr + 4.5));
        }
        g.LeftTicks = left.AsReadOnly();
        var right = new List<ChartTick>();
        if (two)
            foreach (double v in axes[1].Ticks)
            {
                double y = g.Y(v, 1);
                if (y < padT - 1 || y > padT + ph + 1) continue;
                right.Add(new ChartTick(v, y, JsNumber.ToFixed(v, axes[1].Dec), Round1(y + 4.5)));
            }
        g.RightTicks = right.AsReadOnly();

        // 横轴刻度
        double xspan = x1 - x0;
        double step = TimeSteps[TimeSteps.Count - 1];
        foreach (double st in TimeSteps)
            if (xspan / st <= Math.Max(3, Math.Floor(pw / 72))) { step = st; break; }
        g.TimeStep = step;
        var tt = new List<ChartTimeTick>();
        for (double t = Math.Ceiling(x0 / step) * step; t <= x1; t += step) tt.Add(new ChartTimeTick(t, g.X(t), ChartTime.Hm(t)));
        g.TimeTicks = tt.AsReadOnly();

        // 曲线的屏幕线段（参考线文字避让用）：[x1, y1, x2, y2, 半线宽]；孤立点按半径 3
        var segs = new List<double[]>();
        foreach (var s in series)
        {
            double hw = s.LineWidth / 2;
            bool havePrev = false;
            double px = 0, py = 0;
            foreach (var p in s.Points)
            {
                if (p.T < x0 || p.T > x1 || !p.Ok) { havePrev = false; continue; }
                double qx = g.X(p.T), qy = g.Y(p.V, s.Axis);
                segs.Add(havePrev ? new[] { px, py, qx, qy, hw } : new[] { qx, qy, qx, qy, 3.0 });
                px = qx; py = qy; havePrev = true;
            }
        }
        g.Segs = segs;

        // 参考线
        var refOut = new List<ChartRefLayout>();
        foreach (var r in refs)
        {
            double y = g.Y(r.V, r.Axis);
            if (y < padT - 0.5 || y > padT + ph + 0.5) continue;
            string col = r.Color ?? ChartTokens.TempSp;
            string lcol = r.LabelColor ?? col;
            if (string.IsNullOrEmpty(r.Label)) { refOut.Add(new ChartRefLayout(r, y, col, null, 0, 0, false, lcol)); continue; }
            double lw = measure(r.Label);
            var cands = new (double X, bool End, double Base)[]
            {
                (padL + pw - 6, true, y - 5), (padL + pw - 6, true, y + 16), (padL + 6, false, y - 5), (padL + 6, false, y + 16),
            };
            (double X, bool End, double Base)? pick = null, first = null;
            foreach (var c in cands)
            {
                double bx0 = c.End ? c.X - lw : c.X;
                double by0 = c.Base - 12, bx1 = bx0 + lw, by1 = c.Base + 3;
                if (!(by0 >= padT + 1 && by1 <= padT + ph - 1)) continue;
                first ??= c;
                if (!Crossed(segs, bx0, by0, bx1, by1)) { pick = c; break; }
            }
            var chosen = pick ?? first ?? (padL + pw - 6, true, y - 5);
            refOut.Add(new ChartRefLayout(r, y, col, r.Label, chosen.X, chosen.Base, chosen.End, lcol));
        }
        g.RefLines = refOut.AsReadOnly();

        // 事件标记
        var mk = new List<ChartMarkerLayout>();
        foreach (var m in o.Markers)
        {
            if (m is null || m.T < x0 || m.T > x1) continue;
            double xv = g.X(m.T);
            string col = m.Color ?? ChartTokens.Mark;
            string lcol = m.LabelColor ?? (col == ChartTokens.Mark ? ChartTokens.MarkInk : col);
            if (string.IsNullOrEmpty(m.Label)) { mk.Add(new ChartMarkerLayout(m, xv, col, null, 0, 0, false, lcol)); continue; }
            bool rightSide = xv > padL + pw - Math.Max(80, measure(m.Label) + 10);
            mk.Add(new ChartMarkerLayout(m, xv, col, m.Label, rightSide ? xv - 5 : xv + 5, padT + 15, rightSide, lcol));
        }
        g.Markers = mk.AsReadOnly();

        // 画线顺序：虚线先、实线后（稳定排序）
        var order = new List<int>(series.Count);
        for (int i = 0; i < series.Count; i++) if (!string.IsNullOrEmpty(series[i].Dash)) order.Add(i);
        for (int i = 0; i < series.Count; i++) if (string.IsNullOrEmpty(series[i].Dash)) order.Add(i);
        g.DrawOrder = order.AsReadOnly();
        g.EmptyText = series.Count == 0 ? (string.IsNullOrEmpty(o.EmptyText) ? "暂无数据" : o.EmptyText) : null;
        g.TipGapMs = o.TipGapMs ?? 30000;
        return g;
    }

    /// <summary>
    /// 悬停（样稿 onMove）：<paramref name="x"/> / <paramref name="y"/> 在绘图区外、或没有系列覆盖这个时刻时返回 null。
    /// 先在点数最多的覆盖系列里找最近的点（二分），十字线对齐到它的时刻；再给每个覆盖这个时刻的系列找最近点（缺失值跳过）。
    /// 读数的小数位：系列的 Dec，否则 max(轴的小数位, 1)。
    /// </summary>
    public static ChartHover? Hover(ChartGeometry g, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (!g.InPlot(x, y) || g.Series.Count == 0) return null;
        double t = g.TimeAt(x);
        double gap = g.TipGapMs;
        int refS = -1;
        for (int i = 0; i < g.Series.Count; i++)
        {
            var pts = g.Series[i].Points;
            if (pts.Count == 0 || t < pts[0].T - gap || t > pts[pts.Count - 1].T + gap) continue;
            if (refS < 0 || pts.Count > g.Series[refS].Points.Count) refS = i;
        }
        if (refS < 0) return null;
        int ti = NearestIndex(g.Series[refS].Points, t);
        double tt = ti >= 0 ? g.Series[refS].Points[ti].T : t;
        var rows = new List<ChartTipRow>();
        for (int i = 0; i < g.Series.Count; i++)
        {
            var s = g.Series[i];
            var pts = s.Points;
            if (pts.Count == 0 || tt < pts[0].T - gap || tt > pts[pts.Count - 1].T + gap) continue;
            int k = NearestIndex(pts, tt);
            if (k < 0 || !pts[k].Ok) continue;
            var ax = g.Axes[s.Axis >= 0 && s.Axis < g.Axes.Count ? s.Axis : 0];
            int dec = s.Dec ?? Math.Max(ax.Dec, 1);
            rows.Add(new ChartTipRow(i, s.Name, s.Color ?? ChartTokens.S1, JsNumber.ToFixed(pts[k].V, dec), !string.IsNullOrEmpty(s.Unit) ? s.Unit : ax.Unit, g.X(pts[k].T), g.Y(pts[k].V, s.Axis)));
        }
        return new ChartHover(g.X(tt), tt, ChartTime.Time(tt), rows.AsReadOnly());
    }

    /// <summary>提示框的左边（样稿）：右边放不下（十字线 + 16 + 框宽 &gt; 图宽）就放到十字线左边；至少 4。</summary>
    public static double TipLeft(ChartGeometry g, double crossX, double tipWidth)
    {
        double cx = Round1(crossX);
        double left = cx + 16 + tipWidth > g.Width ? cx - 16 - tipWidth : cx + 16;
        return Math.Max(4, left);
    }

    /// <summary>提示框的上边（样稿）：指针上方 30，夹在 [绘图区顶, 图高 − 框高 − 4] 内。</summary>
    public static double TipTop(ChartGeometry g, double pointerY, double tipHeight) => Math.Max(g.PadT, Math.Min(g.Height - tipHeight - 4, pointerY - 30));

    /// <summary>最近的点（样稿 nearestIdx：按时刻二分，两边一样近取前一个）；空表 −1。</summary>
    public static int NearestIndex(IReadOnlyList<ChartPoint> pts, double t)
    {
        ArgumentNullException.ThrowIfNull(pts);
        int lo = 0, hi = pts.Count - 1;
        if (hi < 0) return -1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (pts[mid].T < t) lo = mid; else hi = mid;
        }
        return Math.Abs(pts[lo].T - t) <= Math.Abs(pts[hi].T - t) ? lo : hi;
    }

    /// <summary>刻度文字占的宽度（样稿 tickW）：最长的刻度文字字符数 × 8 + 14。</summary>
    public static double TickWidth(ChartAxisLayout ax)
    {
        ArgumentNullException.ThrowIfNull(ax);
        int n = 0;
        foreach (double v in ax.Ticks) n = Math.Max(n, JsNumber.ToFixed(v, ax.Dec).Length);
        return ax.Ticks.Count == 0 ? double.NegativeInfinity : n * 8 + 14;
    }

    /// <summary>矩形（屏幕 px）是否被某条曲线穿过（含半线宽 + 1.5 px），样稿 crossed。</summary>
    private static bool Crossed(List<double[]> segs, double bx0, double by0, double bx1, double by1)
    {
        foreach (var gs in segs)
        {
            double pad = gs[4] + 1.5;
            double lo = Math.Max(bx0 - pad, Math.Min(gs[0], gs[2])), hi = Math.Min(bx1 + pad, Math.Max(gs[0], gs[2]));
            if (lo > hi) continue;
            double ya, yb;
            if (gs[2] == gs[0]) { ya = Math.Min(gs[1], gs[3]); yb = Math.Max(gs[1], gs[3]); }
            else
            {
                ya = gs[1] + (gs[3] - gs[1]) * (lo - gs[0]) / (gs[2] - gs[0]);
                yb = gs[1] + (gs[3] - gs[1]) * (hi - gs[0]) / (gs[2] - gs[0]);
            }
            if (Math.Max(ya, yb) >= by0 - pad && Math.Min(ya, yb) <= by1 + pad) return true;
        }
        return false;
    }

    /// <summary>JS 的 <c>+v.toFixed(1)</c>。</summary>
    internal static double Round1(double v) => double.Parse(JsNumber.ToFixed(v, 1), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>没有量字体时的文字宽度估计（14 px、600）：ASCII 8.4 px，其它 14 px。</summary>
    public static double EstimateWidth(string text)
    {
        double w = 0;
        foreach (char ch in text ?? "") w += ch < 0x80 ? 8.4 : 14;
        return w;
    }
}
