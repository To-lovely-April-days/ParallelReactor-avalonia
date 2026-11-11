using System;
using System.Collections.Generic;
using ParallelReactor.Control.Core.Chart;
using CoreChartGeometry = ParallelReactor.Control.Core.Chart.ChartGeometry;
using CoreChartOptions = ParallelReactor.Control.Core.Chart.ChartOptions;

namespace ParallelReactor.Control.Controls.Chart;

/// <summary>一笔绘制的种类（样稿 lineChart 画出来的 SVG 元素）：绘图区底、线、文字、曲线、末端圆点。</summary>
public enum ChartOpKind : byte { Plot, Line, Text, Series, EndDot }

/// <summary>线的样式（样稿 base.css 的类）：水平网格（.pr-chart-grid）、竖网格（--v）、坐标框（.pr-chart-axis 1.5）、参考线（.pr-chart-ref 1.4 虚线 6 4）、事件标记（.pr-chart-marker 1.4 虚线 5 4）。</summary>
public enum ChartLineKind : byte { Grid, GridV, Axis, Ref, Marker }

/// <summary>文字的样式：刻度 13（等宽数字）、轴标题 14/500、参考线文字 14/600 + 描边、标记文字 14/600 + 描边、空数据 15。</summary>
public enum ChartTextKind : byte { Tick, AxisTitle, RefLabel, MarkerLabel, Empty }

/// <summary>SVG 的 text-anchor：x 是文字的起点 / 中点 / 终点。</summary>
public enum ChartAnchor : byte { Start, Middle, End }

/// <summary>按顺序的一笔：种类 + 在对应列表里的下标。</summary>
public readonly record struct ChartOp(ChartOpKind Kind, int Index);

/// <summary>一条线（设计 px，同样稿 SVG 的 x1 / y1 / x2 / y2 —— 已按样稿 toFixed(1) 取过）；颜色 = 令牌名（网格 / 坐标框为空，按样式取）；虚线 = 参考线 dash !== false。</summary>
public readonly record struct ChartSceneLine(ChartLineKind Kind, double X1, double Y1, double X2, double Y2, string? Color, bool Dashed);

/// <summary>一段文字：x / y = SVG 的锚点与<b>基线</b>（同样稿 text 元素），颜色 = 令牌名。</summary>
public readonly record struct ChartSceneText(ChartTextKind Kind, string Text, double X, double Y, ChartAnchor Anchor, string Color);

/// <summary>一条曲线：系列下标、颜色令牌、线宽、虚线（SVG px；null = 实线）、点在 <see cref="ChartScene.Xs"/> / <see cref="ChartScene.Ys"/> 里的区间（NaN = 断开）。</summary>
public readonly record struct ChartScenePath(int Series, string Color, double Width, string? Dash, int Start, int Count);

/// <summary>末端圆点（半径 3，系列色填充）。</summary>
public readonly record struct ChartSceneDot(double X, double Y, string Color);

/// <summary>
/// 一张曲线图要画的东西（样稿 js/ui.js lineChart draw() 拼出来的 SVG，按元素的先后顺序）：Core 的 <see cref="CoreChartGeometry"/> 算好了几何，
/// 这里只决定画什么、按什么顺序、坐标怎么取整（样稿写进 SVG 的是 toFixed(1) 还是原数），不碰 Avalonia 的绘图 —— 测试把它拼回 SVG 与样稿逐元素比较。
/// <list type="number">
/// <item>绘图区底（--ch-bg）；</item>
/// <item>左轴：每个刻度一条水平网格线 + 刻度字（右对齐在 padL − 8，基线 = 线的 y + 4.5）；右轴刻度字（左对齐在 padL + pw + 8）；</item>
/// <item>横轴：每个刻度一条竖网格线 + 时间字（居中，基线 padT + ph + 21）；</item>
/// <item>坐标框：底边 + 左边（两条轴时再加右边）；轴标题（左上 / 右上，基线 padT − 14）、横轴标题；</item>
/// <item>参考线、事件标记（线；文字留到最后）；</item>
/// <item>曲线：虚线（设定值 / 目标）先画、实线（实测值）后画，各带末端圆点；</item>
/// <item>参考线文字、标记文字（带 --ch-bg 描边，画在曲线上面）；空数据文字。</item>
/// </list>
/// 列表在每次重建时清空复用（R10：每秒的刷新不重新分配列表）。
/// </summary>
public sealed class ChartScene
{
    private double[] _xs = new double[64], _ys = new double[64];
    private double[] _px = Array.Empty<double>(), _py = Array.Empty<double>();
    private readonly List<ChartSceneText> _labels = new();

    public bool Drawn { get; private set; }
    public double Width { get; private set; }
    public double Height { get; private set; }
    /// <summary>绘图区（padL, padT, pw, ph）。</summary>
    public double PlotX { get; private set; }
    public double PlotY { get; private set; }
    public double PlotW { get; private set; }
    public double PlotH { get; private set; }

    public List<ChartOp> Ops { get; } = new();
    public List<ChartSceneLine> Lines { get; } = new();
    public List<ChartSceneText> Texts { get; } = new();
    public List<ChartScenePath> Paths { get; } = new();
    public List<ChartSceneDot> Dots { get; } = new();
    /// <summary>曲线的点（全部曲线连在一起，区间见 <see cref="ChartScenePath"/>；NaN = 断开）。</summary>
    public double[] Xs => _xs;
    public double[] Ys => _ys;
    /// <summary>点数（<see cref="Xs"/> 里有效的长度）。</summary>
    public int PointCount { get; private set; }

    /// <summary>
    /// 样稿 toFixed(1)（写进 SVG 的坐标）：按 double 的<b>精确</b>值取一位小数，正好一半时取大的（JS Number.prototype.toFixed；符号单独处理）——
    /// 不能用 Math.Round(v × 10)：1.15 的精确值是 1.1499999…，JS 给「1.1」，而 v × 10 先舍入成 11.5 会得到 1.2。
    /// 坐标的量级（&lt; 2^40）用 64 位整数精确算 floor(10·x + ½)，不分配；极小 / 极大的值走 Core 的 JsNumber。
    /// </summary>
    public static double F1(double v)
    {
        if (!double.IsFinite(v)) return v;
        bool neg = v < 0;
        double x = neg ? -v : v;
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0 && frac == 0) return 0;
        long mant = exp == 0 ? frac : frac | (1L << 52);
        int e = (exp == 0 ? 1 : exp) - 1075;   // x = mant × 2^e
        double r;
        if (e >= 0) r = x;                        // 整数（≥ 2^52）
        else if (-e <= 59)
        {
            // floor((10·mant + 2^(−e−1)) / 2^(−e))：10·mant < 2^57，加上半格仍在 long 之内
            long q = (10 * mant + (1L << (-e - 1))) >> -e;
            r = q / 10.0;
        }
        else r = double.Parse(Core.Format.JsNumber.ToFixed(x, 1), System.Globalization.CultureInfo.InvariantCulture);
        return neg && r != 0 ? -r : r;
    }

    /// <summary>什么都不画（没有输入）。</summary>
    public void Clear()
    {
        Ops.Clear();
        Lines.Clear();
        Texts.Clear();
        Paths.Clear();
        Dots.Clear();
        _labels.Clear();
        PointCount = 0;
        Drawn = false;
    }

    /// <summary>按版面重建（清空后按样稿的顺序填）。<paramref name="g"/> 没画（容器 &lt; 40 × 40）时什么都没有。</summary>
    public void Build(CoreChartGeometry g, CoreChartOptions o)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(o);
        Clear();
        Drawn = g.Drawn;
        Width = g.Width;
        Height = g.Height;
        if (!g.Drawn) return;
        double padL = g.PadL, padT = g.PadT, pw = g.Pw, ph = g.Ph, H = g.Height;
        PlotX = padL; PlotY = padT; PlotW = pw; PlotH = ph;
        Ops.Add(new ChartOp(ChartOpKind.Plot, 0));

        // 左轴刻度：网格线 + 字（样稿：y = Y(v).toFixed(1)，字的基线 = +y + 4.5）
        foreach (var t in g.LeftTicks)
        {
            double y = F1(t.Y);
            Line(ChartLineKind.Grid, padL, y, padL + pw, y, null, false);
            Text(ChartTextKind.Tick, t.Text, padL - 8, t.TextY, ChartAnchor.End, ChartStyle.TickInk);
        }
        // 右轴刻度：只有字（超出绘图区的 Core 已经去掉）
        foreach (var t in g.RightTicks)
            Text(ChartTextKind.Tick, t.Text, padL + pw + 8, F1(t.TextY), ChartAnchor.Start, ChartStyle.TickInk);
        // 横轴：竖网格线 + 时间
        foreach (var t in g.TimeTicks)
        {
            double x = F1(t.X);
            Line(ChartLineKind.GridV, x, padT, x, padT + ph, null, false);
            Text(ChartTextKind.Tick, t.Text, x, padT + ph + 21, ChartAnchor.Middle, ChartStyle.TickInk);
        }
        // 坐标框：底边 + 左边（两条轴时 + 右边）
        Line(ChartLineKind.Axis, padL, padT + ph, padL + pw, padT + ph, null, false);
        Line(ChartLineKind.Axis, padL, padT, padL, padT + ph, null, false);
        if (g.TwoAxes) Line(ChartLineKind.Axis, padL + pw, padT, padL + pw, padT + ph, null, false);
        // 轴标题（左上起点 / 右上终点，基线 padT − 14）与横轴标题
        var a0 = g.Axes[0];
        if (!string.IsNullOrEmpty(a0.Label)) Text(ChartTextKind.AxisTitle, a0.Label!, padL, padT - 14, ChartAnchor.Start, a0.Color ?? ChartStyle.TickInk);
        if (g.TwoAxes && !string.IsNullOrEmpty(g.Axes[1].Label))
            Text(ChartTextKind.AxisTitle, g.Axes[1].Label!, padL + pw, padT - 14, ChartAnchor.End, g.Axes[1].Color ?? ChartStyle.TickInk);
        if (!string.IsNullOrEmpty(o.XLabel)) Text(ChartTextKind.AxisTitle, o.XLabel!, padL + pw / 2, H - 8, ChartAnchor.Middle, ChartStyle.TickInk);

        // 参考线 / 事件标记：线在曲线下面，文字留到最后（曲线之上，带底色描边）
        foreach (var r in g.RefLines)
        {
            double y = F1(r.Y);
            Line(ChartLineKind.Ref, padL, y, padL + pw, y, r.Color, r.Line.Dash);
            if (r.Label is not null)
                _labels.Add(new ChartSceneText(ChartTextKind.RefLabel, r.Label, F1(r.LabelX), F1(r.LabelBase), r.LabelEnd ? ChartAnchor.End : ChartAnchor.Start, r.LabelColor));
        }
        foreach (var m in g.Markers)
        {
            double x = F1(m.X);
            Line(ChartLineKind.Marker, x, padT, x, padT + ph, m.Color, true);
            if (m.Label is not null)
                _labels.Add(new ChartSceneText(ChartTextKind.MarkerLabel, m.Label, F1(m.LabelX), m.LabelY, m.LabelEnd ? ChartAnchor.End : ChartAnchor.Start, m.LabelColor));
        }

        // 曲线：虚线（设定值 / 目标）先、实线后（Core 的 DrawOrder），各自的末端圆点紧跟着；窗口里一个有效点都没有的不画（也没有圆点）
        foreach (int i in g.DrawOrder)
        {
            var s = g.Series[i];
            int k = g.ScreenPoints(i, ref _px, ref _py);
            int start = PointCount, n = 0;
            bool any = false;
            for (int j = 0; j < k; j++)
            {
                bool gap = double.IsNaN(_px[j]);
                Push(start + n, gap ? double.NaN : F1(_px[j]), gap ? double.NaN : F1(_py[j]));
                n++;
                any |= !gap;
            }
            if (!any) continue;
            PointCount = start + n;
            Paths.Add(new ChartScenePath(i, s.Color ?? ChartTokens.S1, s.LineWidth, string.IsNullOrEmpty(s.Dash) ? null : s.Dash, start, n));
            Ops.Add(new ChartOp(ChartOpKind.Series, Paths.Count - 1));
            if (g.EndDot(i) is { } e)
            {
                Dots.Add(new ChartSceneDot(F1(e.X), F1(e.Y), s.Color ?? ChartTokens.S1));
                Ops.Add(new ChartOp(ChartOpKind.EndDot, Dots.Count - 1));
            }
        }
        foreach (var t in _labels) Text(t.Kind, t.Text, t.X, t.Y, t.Anchor, t.Color);
        if (g.EmptyText is { } empty) Text(ChartTextKind.Empty, empty, padL + pw / 2, padT + ph / 2, ChartAnchor.Middle, ChartStyle.TickInk);
    }

    private void Push(int at, double x, double y)
    {
        if (at >= _xs.Length)
        {
            int size = Math.Max(_xs.Length * 2, at + 1);
            Array.Resize(ref _xs, size);
            Array.Resize(ref _ys, size);
        }
        _xs[at] = x;
        _ys[at] = y;
    }

    private void Line(ChartLineKind kind, double x1, double y1, double x2, double y2, string? color, bool dashed)
    {
        Lines.Add(new ChartSceneLine(kind, x1, y1, x2, y2, color, dashed));
        Ops.Add(new ChartOp(ChartOpKind.Line, Lines.Count - 1));
    }

    private void Text(ChartTextKind kind, string text, double x, double y, ChartAnchor anchor, string color)
    {
        Texts.Add(new ChartSceneText(kind, text, x, y, anchor, color));
        Ops.Add(new ChartOp(ChartOpKind.Text, Texts.Count - 1));
    }
}
