using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Records;

/// <summary>记录页叠加曲线的一条线：RV 号、识别色、虚线（16 路的 RV9–16）、点（距会话开始的分钟 + 值，NaN = 断开）。</summary>
public sealed record RecChartLine(int N, Color Color, bool Dashed, double[] M, double[] V);

/// <summary>
/// 记录页叠加曲线一帧的输入：量名 / 单位 / 小数位、横轴起点（保压类 8 min）与长度（min）、会话开始（刻度写钟点）、各路、平均线（吸气量模式）、选中的一路、保压类、
/// 压力单位相对 MPa 的倍数（压力的值已经是设置的单位；纵轴的最小跨度 0.1 / 步长 0.001 MPa 按它换算；温度 / 吸气量为 1）。
/// </summary>
public sealed record RecChartData(string Title, string Unit, int Decimals, double M0, double Span, DateTime Start,
    IReadOnlyList<RecChartLine> Lines, IReadOnlyList<(double M, double V)> Mean, int? Selected, bool Hold, double Scale = 1);

/// <summary>
/// 记录页的叠加曲线（融合版 F7，样稿 rcChart 的 SVG 逐项照画）：绘图区左 48 右 18 上 22 下 24；横轴从会话开始（保压类从第 8 min）起，
/// 刻度间隔按时长 10 / 15 / 30 / 60 min，写钟点（第一格左对齐）；纵轴 nice 刻度 4 格（矮于 150 时 2 格），吸气量 / 温度 / 压力从 0 起，
/// 保压类按数据的最小值往下取整（小数 4 位）；底线 ch-axis、其余 ch-grid；左上角量名 13/600 ch-title。
/// 每路一条线（识别色，1.5 px，95 % 不透明；选中别的路时 55 %；RV9–16 虚线 6 4）；选中的一路最后画、2.6 px、下面 8 % 面积（保压类不画面积）；
/// 吸气量模式多一条平均线（ch-title 1.6 px 虚线 6 4）。悬停：竖虚线 + 提示框（「12:06 · 吸气量 mmol」+ 各路读数从大到小，最多 8 路）。
/// 自绘文字经 <see cref="IAuditText"/> 报给截图审计。
/// </summary>
public sealed class RecChart : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<RecChartData?> DataProperty = AvaloniaProperty.Register<RecChart, RecChartData?>(nameof(Data));

    public RecChartData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private const double L = 48, R = 18, T = 22, B = 24, Fs = 13;
    private readonly List<AuditTextRun> _audit = new();
    private double? _hoverX;

    static RecChart()
    {
        AffectsRender<RecChart>(DataProperty);
        ClipToBoundsProperty.OverrideDefaultValue<RecChart>(true);
    }

    public RecChart()
    {
        PointerMoved += (_, e) => { _hoverX = e.GetPosition(this).X; InvalidateVisual(); };
        PointerExited += (_, _) => { _hoverX = null; InvalidateVisual(); };
    }

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    /// <summary>悬停的位置（测试 / 截图用：设一个 x 就画十字线和提示框）。</summary>
    public double? HoverX { get => _hoverX; set { _hoverX = value; InvalidateVisual(); } }

    /// <summary>nice 步长（样稿 nice）。</summary>
    public static double Nice(double v)
    {
        if (!(v > 0)) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v))), f = v / e;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * e;
    }

    /// <summary>横轴刻度间隔（样稿：≤ 60 min 10、≤ 120 15、≤ 240 30，否则 60）。</summary>
    public static double TickMinutes(double span) => span <= 60 ? 10 : span <= 120 ? 15 : span <= 240 ? 30 : 60;

    /// <summary>纵轴范围（样稿 rcChart）：返回 (起点, 步长, 终点)。</summary>
    public static (double Y0, double Step, double VMax) YRange(RecChartData d, double ih)
    {
        double lo = double.PositiveInfinity, top = d.Title == "吸气量" ? 5 : .1 * d.Scale;
        bool any = false;
        foreach (var ln in d.Lines)
            for (int i = 0; i < ln.V.Length; i++)
            {
                double v = ln.V[i];
                if (!double.IsFinite(v) || ln.M[i] < d.M0 - 1e-9) continue;
                any = true;
                if (v > top) top = v;
                if (v < lo) lo = v;
            }
        if (!any || !d.Hold) lo = 0;
        int nT = ih >= 150 ? 4 : 2;
        double step = Nice(Math.Max((d.Hold ? .0005 : .001) * d.Scale, (top - lo) * 1.06 / nT));
        double y0 = d.Hold ? Math.Floor(lo / step) * step : 0;
        double vmax = y0 + step * Math.Ceiling((top - y0) / step - 1e-9);
        if (vmax <= y0) vmax = y0 + step;
        return (y0, step, vmax);
    }

    private Typeface Face(FontWeight w) => new(TextElement.GetFontFamily(this), FontStyle.Normal, w);

    private FormattedText Text(string s, IBrush brush, FontWeight w = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face(w), Fs, brush);

    private void Draw(DrawingContext ctx, string s, double x, double baseline, int anchor, IBrush brush, Color c, FontWeight w = FontWeight.Normal)
    {
        var t = Text(s, brush, w);
        double x0 = anchor < 0 ? x : anchor == 0 ? x - t.Width / 2 : x - t.Width;
        double y0 = baseline - t.Baseline;
        ctx.DrawText(t, new Point(x0, y0));
        _audit.Add(new AuditTextRun(s, Fs, new Rect(x0, y0, t.Width, t.Height), c));
    }

    public override void Render(DrawingContext ctx)
    {
        _audit.Clear();
        var d = Data;
        double W = Bounds.Width, H = Bounds.Height;
        if (d == null || W < 160 || H < 90) return;
        double iw = W - L - R, ih = H - T - B, span = Math.Max(d.M0 + 1, d.Span);
        var (y0, step, vmax) = YRange(d, ih);
        double X(double m) => L + (m - d.M0) / (span - d.M0) * iw;
        double Y(double v) => T + ih - (v - y0) / (vmax - y0) * ih;

        var grid = Palette.Pen(Palette.ChGridColor, 1);
        double tick = TickMinutes(span);
        for (double m = d.M0; m <= span + .01; m += tick)
        {
            double x = Math.Round(X(m)) + 0.5;
            ctx.DrawLine(grid, new Point(x, T), new Point(x, T + ih));
            Draw(ctx, Core.Records.RecTexts.Clock(d.Start, m), x, T + ih + 18, m == d.M0 ? -1 : 0, Palette.ChTickBrush, Palette.ChTickColor);
        }
        int dec = d.Hold ? 4 : 2;
        for (double v = y0; v <= vmax + 1e-9; v += step)
        {
            double y = Math.Round(Y(v)) + 0.5;
            ctx.DrawLine(Math.Abs(v - y0) < 1e-12 ? Palette.Pen(Palette.ChAxisColor, 1) : grid, new Point(L, y), new Point(L + iw, y));
            Draw(ctx, Math.Round(v, dec).ToString("0.####", CultureInfo.InvariantCulture), L - 7, y + 4, 1, Palette.ChTickBrush, Palette.ChTickColor);
        }
        Draw(ctx, d.Title + " " + d.Unit, L - 40, T - 8, -1, Palette.ChTitleBrush, Palette.ChTitleColor, FontWeight.SemiBold);

        using (ctx.PushClip(new Rect(L - 3, T - 3, iw + 6, ih + 6)))
        {
            var sel = d.Lines.FirstOrDefault(l => l.N == d.Selected);
            if (sel != null && !d.Hold && Path(sel, d.M0, X, Y, Y(y0)) is { } area)
                ctx.DrawGeometry(new SolidColorBrush(sel.Color, 0.08), null, area);
            foreach (var ln in d.Lines.OrderBy(l => l.N == d.Selected ? 1 : 0))
            {
                bool on = ln.N == d.Selected;
                if (Path(ln, d.M0, X, Y, null) is not { } g) continue;
                double op = d.Selected != null && !on ? .55 : .95;
                ctx.DrawGeometry(null, Palette.Pen(Palette.WithAlpha(ln.Color, op), on ? 2.6 : 1.5, ln.Dashed ? "6 4" : null, PenLineCap.Flat), g);
            }
            if (d.Mean.Count > 1)
            {
                var mg = new StreamGeometry();
                using (var c = mg.Open())
                {
                    bool open = false;
                    foreach (var (m, v) in d.Mean)
                    {
                        if (m < d.M0 - 1e-9) continue;
                        var p = new Point(X(m), Y(v));
                        if (!open) { c.BeginFigure(p, false); open = true; } else c.LineTo(p);
                    }
                    if (open) c.EndFigure(false);
                }
                ctx.DrawGeometry(null, Palette.Pen(Palette.ChTitleColor, 1.6, "6 4", PenLineCap.Flat), mg);
            }
        }
        if (_hoverX is double hx && hx >= L && hx <= L + iw) DrawHover(ctx, d, hx, span);
    }

    private void DrawHover(DrawingContext ctx, RecChartData d, double hx, double span)
    {
        double iw = Bounds.Width - L - R;
        double m = d.M0 + (hx - L) / iw * (span - d.M0);
        double x = Math.Round(hx) + 0.5;
        ctx.DrawLine(Palette.Pen(Palette.ChTitleColor, 1, "3 3"), new Point(x, T), new Point(x, Bounds.Height - B));
        var rows = new List<(int N, Color C, double V)>();
        foreach (var ln in d.Lines)
        {
            int best = -1;
            for (int i = 0; i < ln.M.Length; i++) { if (ln.M[i] > m) break; best = i; }
            if (best < 0) best = 0;
            if (ln.V.Length > 0 && double.IsFinite(ln.V[best])) rows.Add((ln.N, ln.Color, ln.V[best]));
        }
        if (rows.Count == 0) return;
        rows.Sort((a, b) => b.V.CompareTo(a.V));
        string head = Core.Records.RecTexts.Clock(d.Start, m) + " · " + d.Title + " " + d.Unit;
        var lines = rows.Take(8).Select(r => ("RV" + r.N.ToString(CultureInfo.InvariantCulture) + "　" + r.V.ToString("F" + d.Decimals, CultureInfo.InvariantCulture), (Color?)r.C)).ToList();
        if (rows.Count > 8) lines.Add(("还有 " + (rows.Count - 8).ToString(CultureInfo.InvariantCulture) + " 路", null));
        double w = Text(head, Palette.T1Brush, FontWeight.SemiBold).Width;
        foreach (var (s, _) in lines) w = Math.Max(w, Text(s, Palette.T2Brush).Width + 18);
        w += 20;
        double h = 14 + 16 * (lines.Count + 1) + 3 * lines.Count;
        double bx = hx + 14 + w > Bounds.Width ? hx - 14 - w : hx + 14, by = T + 4;
        var box = new Rect(Math.Round(bx) + 0.5, by + 0.5, Math.Round(w), Math.Round(h));
        ctx.DrawRectangle(Palette.BgCardBrush, Palette.Pen(Palette.LineStrongColor, 1), new RoundedRect(box, 2), Palette.ShadowTip);
        double baseY = box.Y + 7 + 12.5;
        Draw(ctx, head, box.X + 10, baseY, -1, Palette.T1Brush, Palette.T1Color, FontWeight.SemiBold);
        for (int i = 0; i < lines.Count; i++)
        {
            double ly = baseY + 19 * (i + 1);
            var (s, c) = lines[i];
            if (c is Color cc)
            {
                ctx.DrawRectangle(new SolidColorBrush(cc), null, new RoundedRect(new Rect(box.X + 10, ly - 5.5, 12, 3), 1));
                Draw(ctx, s, box.X + 28, ly, -1, Palette.T2Brush, Palette.T2Color);
            }
            else Draw(ctx, s, box.X + 10, ly, -1, Palette.T3Brush, Palette.T3Color);
        }
    }

    /// <summary>折线（NaN 处断开；m0 之前的点不画）；<paramref name="close"/> 给了时画成到这条水平线为止的封闭面积。</summary>
    private static StreamGeometry? Path(RecChartLine ln, double m0, Func<double, double> X, Func<double, double> Y, double? close)
    {
        var g = new StreamGeometry();
        bool any = false;
        using (var c = g.Open())
        {
            bool open = false;
            double fx = 0, lx = 0;
            for (int i = 0; i < ln.M.Length; i++)
            {
                if (ln.M[i] < m0 - 1e-9) continue;
                double v = ln.V[i];
                if (!double.IsFinite(v))
                {
                    if (open) { if (close is double yc0) { c.LineTo(new Point(lx, yc0)); c.LineTo(new Point(fx, yc0)); c.EndFigure(true); } else c.EndFigure(false); }
                    open = false;
                    continue;
                }
                var p = new Point(X(ln.M[i]), Y(v));
                if (!open) { c.BeginFigure(p, close.HasValue); fx = p.X; open = true; any = true; } else c.LineTo(p);
                lx = p.X;
            }
            if (open) { if (close is double yc) { c.LineTo(new Point(lx, yc)); c.LineTo(new Point(fx, yc)); c.EndFigure(true); } else c.EndFigure(false); }
        }
        return any ? g : null;
    }
}

/// <summary>
/// 结果表「与平均相比」的条（样稿 .dbar：宽 64（窄屏 48）、高 10，中线 1 px ch-axis；高于平均 = 品牌蓝向右、低于 = 灰 n-dot-line 向左，圆角 2、上下各缩 1）。
/// <see cref="Value"/> = 相对偏差 ÷ 本表最大偏差，−1…1。
/// </summary>
public sealed class DivBar : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<DivBar, double>(nameof(Value));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    static DivBar() => AffectsRender<DivBar>(ValueProperty);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, mid = Math.Round(w / 2);
        ctx.FillRectangle(Palette.ChAxisBrush, new Rect(mid, 0, 1, h));
        double v = Math.Clamp(Value, -1, 1), bw = Math.Abs(v) * w / 2;
        if (bw < 0.5) return;
        var r = v >= 0 ? new Rect(mid, 1, bw, h - 2) : new Rect(mid - bw, 1, bw, h - 2);
        ctx.DrawRectangle(v >= 0 ? Palette.ABrush : Palette.NDotLineBrush, null, new RoundedRect(r, 2));
    }
}
