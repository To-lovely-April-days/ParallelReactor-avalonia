using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Home;

/// <summary>
/// 8 路叠加曲线（融合版 F3，样稿 drawOv 的 SVG 逐项照画）：绘图区左 48 右 92 上 24 下 24；横轴每 15 min 一格、右端写「现在」；纵轴 4 格（矮于 150 时 2 格）nice 刻度，
/// 底线 ch-axis、其余 ch-grid；左上角量名「吸气量 mmol」13/600 ch-title。每路一条线（识别色，1.5 px、85 % 不透明；16 路的 RV9–16 同色虚线 6 4），
/// 选中的一路最后画、2.6 px、下面 8 % 面积填充、末端 4 px 圆点 + 右侧值标签（白底、识别色细边、「RV1 18.4」13/600）；其余末端 2.6 px 圆点。
/// 压力模式画上限红虚线 + 「上限 3.50」。悬停：竖虚线 + 提示框（「13:45 · 吸气量 mmol」+ 各路读数，从大到小，最多 8 路）。
/// 自绘文字经 <see cref="IAuditText"/> 报给截图审计。
/// </summary>
public sealed class OverlayChart : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<OverlayData?> DataProperty = AvaloniaProperty.Register<OverlayChart, OverlayData?>(nameof(Data));

    public OverlayData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private const double L = 48, R = 92, T = 24, B = 24, Fs = 13;
    private readonly List<AuditTextRun> _audit = new();
    private double? _hoverX;

    static OverlayChart()
    {
        AffectsRender<OverlayChart>(DataProperty);
        ClipToBoundsProperty.OverrideDefaultValue<OverlayChart>(true);
    }

    public OverlayChart()
    {
        PointerMoved += (_, e) => { _hoverX = e.GetPosition(this).X; InvalidateVisual(); };
        PointerExited += (_, _) => { _hoverX = null; InvalidateVisual(); };
    }

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    /// <summary>nice 步长（样稿 nice：1 / 2 / 2.5 / 5 / 10 × 10^k）。</summary>
    public static double Nice(double v)
    {
        if (!(v > 0)) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v))), f = v / e;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * e;
    }

    private Typeface Face(FontWeight w) => new(TextElement.GetFontFamily(this), FontStyle.Normal, w);

    private FormattedText Text(string s, IBrush brush, FontWeight w = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face(w), Fs, brush);

    /// <summary>画一段字：<paramref name="x"/> 按 <paramref name="anchor"/>（-1 左 / 0 中 / 1 右）对齐，<paramref name="baseline"/> = SVG 的 y（基线）。</summary>
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
        double iw = W - L - R, ih = H - T - B, span = Math.Max(1, d.T1 - d.T0);
        int nT = ih >= 150 ? 4 : 2;
        double top = d.MinTop > 0 ? d.MinTop : d.Mode == Pages.Home.OverlayMode.G ? 5 : 1;
        if (d.Limit is double lim0) top = Math.Max(top, lim0);
        foreach (var ln in d.Lines)
            for (int i = 0; i < ln.Count; i++) if (double.IsFinite(ln.Points[i].V)) top = Math.Max(top, ln.Points[i].V);
        double step = Nice(top * 1.06 / nT), vmax = step * nT;
        double X(double t) => L + (t - d.T0) / span * iw;
        double Y(double v) => T + ih - v / vmax * ih;

        // 网格与横轴：每 15 min 一格（从窗口起点算），右端「现在」
        var grid = Palette.Pen(Palette.ChGridColor, 1);
        double minutes = span / 60000;
        double tick = minutes > 180 ? 30 : 15;
        for (double m = 0; m <= minutes + 1e-6; m += tick)
        {
            double x = Math.Round(X(d.T0 + m * 60000)) + 0.5;
            if (W - R - x < 40 && m < minutes - 1e-6) continue;
            ctx.DrawLine(grid, new Point(x, T), new Point(x, T + ih));
            Draw(ctx, d.Start.AddMinutes(m).ToString("HH:mm", CultureInfo.InvariantCulture), x, T + ih + 18, 0, Palette.ChTickBrush, Palette.ChTickColor);
        }
        {
            double x = Math.Round(L + iw) + 0.5;
            ctx.DrawLine(grid, new Point(x, T), new Point(x, T + ih));
            Draw(ctx, "现在", x, T + ih + 18, 1, Palette.ChTickBrush, Palette.ChTickColor);
        }
        for (int i = 0; i <= nT; i++)
        {
            double v = step * i, y = Math.Round(Y(v)) + 0.5;
            ctx.DrawLine(i == 0 ? Palette.Pen(Palette.ChAxisColor, 1) : grid, new Point(L, y), new Point(L + iw, y));
            Draw(ctx, Fmt(v), L - 7, y + 4, 1, Palette.ChTickBrush, Palette.ChTickColor);
        }
        Draw(ctx, d.Title + " " + d.Unit, L - 40, T - 8, -1, Palette.ChTitleBrush, Palette.ChTitleColor, FontWeight.SemiBold);

        if (d.Limit is double lim)
        {
            double y = Y(lim);
            ctx.DrawLine(Palette.Pen(Palette.WithAlpha(Palette.ChLimitColor, 0.6), 1, "3 4"), new Point(L, y), new Point(L + iw, y));
            Draw(ctx, "上限 " + lim.ToString("0.00", CultureInfo.InvariantCulture), L + iw + 6, y + 4, -1, Palette.RInkBrush, Palette.RInkColor);
        }

        // 选中的一路：面积填充（8 %），最后画线
        var selLine = d.Lines.FirstOrDefault(l => l.N == d.Selected);
        if (selLine != null)
        {
            var area = Path(selLine, X, Y, close: Y(0));
            if (area != null) ctx.DrawGeometry(new SolidColorBrush(selLine.Color, 0.08), null, area);
        }
        foreach (var ln in d.Lines.OrderBy(l => l.N == d.Selected ? 1 : 0))
        {
            bool sel = ln.N == d.Selected;
            var g = Path(ln, X, Y, close: null);
            if (g == null) continue;
            var pen = Palette.Pen(sel ? ln.Color : Palette.WithAlpha(ln.Color, 0.85), sel ? 2.6 : 1.5, ln.Dashed ? (sel ? "2.3 1.5" : "4 2.6667") : null, PenLineCap.Flat);
            ctx.DrawGeometry(null, pen, g);
            if (LastOk(ln) is { } p)
                ctx.DrawEllipse(new SolidColorBrush(ln.Color), Palette.Pen(Palette.BgCardColor, 1.5), new Point(X(p.T), Y(p.V)), sel ? 4 : 2.6, sel ? 4 : 2.6);
        }
        // 选中一路的值标签
        if (selLine != null && LastOk(selLine) is { } lp)
        {
            var rect = new Rect(L + iw + 8, Math.Round(Y(lp.V) - 11) + 0.5, R - 12, 22);
            ctx.DrawRectangle(Palette.BgCardBrush, Palette.Pen(selLine.Color, 1), new RoundedRect(rect, 3));
            Draw(ctx, "RV" + selLine.N.ToString(CultureInfo.InvariantCulture) + " " + lp.V.ToString("F" + d.Decimals, CultureInfo.InvariantCulture),
                rect.X + rect.Width / 2, rect.Y + 15, 0, Palette.T1Brush, Palette.T1Color, FontWeight.SemiBold);
        }

        if (_hoverX is double hx && hx >= L && hx <= L + iw) DrawHover(ctx, d, hx, X, Y);
    }

    private void DrawHover(DrawingContext ctx, OverlayData d, double hx, Func<double, double> X, Func<double, double> Y)
    {
        double t = d.T0 + (hx - L) / (Bounds.Width - L - R) * (d.T1 - d.T0);
        ctx.DrawLine(Palette.Pen(Palette.ChMarkColor, 1, "3 3"), new Point(hx, T), new Point(hx, Bounds.Height - B));
        var rows = new List<(int N, Color C, double V)>();
        foreach (var ln in d.Lines)
        {
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < ln.Count; i++) { double dd = Math.Abs(ln.Points[i].T - t); if (dd < bd) { bd = dd; best = i; } }
            if (best >= 0 && bd < 60000 && double.IsFinite(ln.Points[best].V)) rows.Add((ln.N, ln.Color, ln.Points[best].V));
        }
        rows.Sort((a, b) => b.V.CompareTo(a.V));
        var head = d.Start.AddMilliseconds(t - d.T0).ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + d.Title + " " + d.Unit;
        var lines = rows.Take(8).Select(r => ("RV" + r.N.ToString(CultureInfo.InvariantCulture) + "　" + r.V.ToString("F" + d.Decimals, CultureInfo.InvariantCulture), (Color?)r.C)).ToList();
        if (rows.Count > 8) lines.Add(("还有 " + (rows.Count - 8).ToString(CultureInfo.InvariantCulture) + " 路", null));
        double w = Text(head, Palette.T1Brush, FontWeight.SemiBold).Width;
        foreach (var (s, _) in lines) w = Math.Max(w, Text(s, Palette.T2Brush).Width + 16);
        w += 20;
        double h = 10 + 20 * (lines.Count + 1);
        double x = hx + 14 + w > Bounds.Width ? hx - 14 - w : hx + 14, y = T + 4;
        var box = new Rect(x, y, w, h);
        ctx.DrawRectangle(Palette.BgCardBrush, Palette.Pen(Palette.LineSoftColor, 1), new RoundedRect(box, 2), Palette.ShadowTip);
        Draw(ctx, head, x + 10, y + 19, -1, Palette.T1Brush, Palette.T1Color, FontWeight.SemiBold);
        for (int i = 0; i < lines.Count; i++)
        {
            double by = y + 19 + 20 * (i + 1);
            var (s, c) = lines[i];
            if (c is Color cc)
            {
                ctx.DrawEllipse(new SolidColorBrush(cc), null, new Point(x + 14, by - 4.5), 4, 4);
                Draw(ctx, s, x + 24, by, -1, Palette.T2Brush, Palette.T2Color);
            }
            else Draw(ctx, s, x + 10, by, -1, Palette.T3Brush, Palette.T3Color);
        }
    }

    private static ChartPoint? LastOk(OverlayLine ln)
    {
        for (int i = ln.Count - 1; i >= 0; i--) if (double.IsFinite(ln.Points[i].V)) return ln.Points[i];
        return null;
    }

    /// <summary>折线（NaN 处断开）；<paramref name="close"/> 给了时画成到这条水平线为止的封闭面积（只取最后一段连续的）。</summary>
    private static StreamGeometry? Path(OverlayLine ln, Func<double, double> X, Func<double, double> Y, double? close)
    {
        if (ln.Count == 0) return null;
        var g = new StreamGeometry();
        bool any = false;
        using (var c = g.Open())
        {
            bool open = false;
            double fx = 0, lx = 0;
            for (int i = 0; i < ln.Count; i++)
            {
                var p = ln.Points[i];
                if (!double.IsFinite(p.V))
                {
                    if (open && close is double y0) { c.LineTo(new Point(lx, y0)); c.LineTo(new Point(fx, y0)); c.EndFigure(true); }
                    else if (open) c.EndFigure(false);
                    open = false;
                    continue;
                }
                var pt = new Point(X(p.T), Y(p.V));
                if (!open) { c.BeginFigure(pt, close.HasValue); fx = pt.X; open = true; any = true; }
                else c.LineTo(pt);
                lx = pt.X;
            }
            if (open && close is double yc) { c.LineTo(new Point(lx, yc)); c.LineTo(new Point(fx, yc)); c.EndFigure(true); }
            else if (open) c.EndFigure(false);
        }
        return any ? g : null;
    }

    private static string Fmt(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
