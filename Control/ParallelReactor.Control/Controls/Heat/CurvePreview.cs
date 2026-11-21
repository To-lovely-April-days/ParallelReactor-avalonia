using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Heat;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Heat;

/// <summary>
/// 曲线窗口的预览图（设计稿 cvChart，500 × 316）：横轴 = 启动后的时间（左端写钟点，其余「+30 min」「+1 h」），纵轴 0 … 上限温度 4 格；
/// 给定值折线（从当前实测温度起，线下淡蓝）+ 每段末端的空心点和温度；运行中折线改虚线，已走过的部分加粗，竖虚线「现在 14:31」，绿圈 = 实测温度。
/// 文字固定 13 px（本机最小字号），不随宽度缩放。
/// </summary>
public sealed class CurvePreview : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<CurvePlot?> PlotProperty = AvaloniaProperty.Register<CurvePreview, CurvePlot?>(nameof(Plot));

    public CurvePlot? Plot { get => GetValue(PlotProperty); set => SetValue(PlotProperty, value); }

    private const double L = 46, R = 16, T = 34, B = 34, Fs = 13;
    private readonly List<AuditTextRun> _audit = new();

    static CurvePreview() => AffectsRender<CurvePreview>(PlotProperty);

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) ? availableSize.Width : 500;
        return new Size(w, Math.Round(Math.Clamp(w * 316 / 500, 260, 330)));
    }

    /// <summary>横轴刻度的间隔（分钟）：最多 5 格。</summary>
    public static double XStep(double total)
    {
        foreach (double s in new[] { 5.0, 10, 15, 30, 60, 120, 240, 480, 1200, 2400 })
            if (total / s <= 5.01) return s;
        return 2400;
    }

    /// <summary>刻度文字：「+30 min」/「+1 h」。</summary>
    public static string XLabel(double m) => m >= 60 && Math.Abs(m % 60) < 1e-9 ? "+" + (m / 60).ToString("0", CultureInfo.InvariantCulture) + " h" : "+" + m.ToString("0", CultureInfo.InvariantCulture) + " min";

    private Typeface Face(bool bold = false) => new(TextElement.GetFontFamily(this), FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>anchor：−1 左对齐、0 居中、1 右对齐；halo = 字下面垫一块底色（画在线上面的字）。</summary>
    private void Draw(DrawingContext ctx, string s, double x, double baseline, int anchor, IBrush brush, Color c, bool bold = false, bool halo = false)
    {
        var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face(bold), Fs, brush);
        double x0 = anchor < 0 ? x : anchor == 0 ? x - t.Width / 2 : x - t.Width;
        x0 = Math.Clamp(x0, 0, Math.Max(0, Bounds.Width - t.Width));
        double y0 = baseline - t.Baseline;
        if (halo) ctx.DrawRectangle(Palette.BgCardBrush, null, new RoundedRect(new Rect(x0 - 3, y0, t.Width + 6, t.Height), 2));
        ctx.DrawText(t, new Point(x0, y0));
        _audit.Add(new AuditTextRun(s, Fs, new Rect(x0, y0, t.Width, t.Height), c));
    }

    public override void Render(DrawingContext ctx)
    {
        _audit.Clear();
        var d = Plot;
        double W = Bounds.Width, H = Bounds.Height;
        if (d == null || d.Rows.Count == 0 || W < 240 || H < 160) return;
        double pw = W - L - R, ph = H - T - B, total = Math.Max(d.TotalMin, 0.1), ymax = Math.Max(d.TempMaxC, 1);
        double X(double m) => L + m / total * pw;
        double Y(double t) => T + ph - Math.Clamp(t, 0, ymax) / ymax * ph;

        var grid = Palette.Pen(Palette.ChGridColor, 1);
        for (int i = 0; i <= 4; i++)
        {
            double v = ymax * i / 4, y = Math.Round(Y(v)) + 0.5;
            ctx.DrawLine(grid, new Point(L, y), new Point(W - R, y));
            Draw(ctx, Math.Round(v).ToString("0", CultureInfo.InvariantCulture), L - 8, y + 4.5, 1, Palette.T3Brush, Palette.T3Color);
        }
        Draw(ctx, "°C", L - 8, T - 16, 1, Palette.T3Brush, Palette.T3Color);

        double step = XStep(total), y0 = T + ph;
        var tick = Palette.Pen(Palette.ChAxisColor, 1);
        for (double m = 0; m <= total + 1e-6; m += step)
        {
            double x = Math.Round(X(m)) + 0.5;
            ctx.DrawLine(tick, new Point(x, y0), new Point(x, y0 + 4));
            bool last = X(m) > W - R - 30;
            Draw(ctx, m == 0 ? Fmt.Hm(d.StartAt) : XLabel(m), X(m), H - 14, m == 0 ? -1 : last ? 1 : 0, Palette.T3Brush, Palette.T3Color);
        }

        // 给定值折线：起点（当前实测温度）→ 每段末端
        var pts = new List<Point> { new(X(0), Y(d.StartC)) };
        foreach (var r in d.Rows) pts.Add(new Point(X(r.EndMin), Y(r.TempC)));
        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(pts[0], true);
            for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
            g.LineTo(new Point(X(total), y0));
            g.LineTo(new Point(L, y0));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Palette.WithAlpha(Palette.AColor, .07)), null, fill);
        ctx.DrawGeometry(null, d.Running ? Palette.Pen(Palette.AColor, 1.5, "5 4") : new Pen(Palette.ABrush, 2) { LineJoin = PenLineJoin.Round }, Line(pts));

        CurveNow? at = d.Running ? HeatCurve.At(d.Rows, d.ElapsedMin) : null;
        if (at != null)
        {
            double xn = X(d.ElapsedMin);
            var done = new List<Point> { pts[0] };
            foreach (var r in d.Rows) if (r.I < at.Row.I) done.Add(new Point(X(r.EndMin), Y(r.TempC)));
            done.Add(new Point(xn, Y(at.SpC)));
            ctx.DrawGeometry(null, new Pen(Palette.ABrush, 3) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, Line(done));
            double xl = Math.Round(xn) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.T2Color, 1, "3 3"), new Point(xl, T), new Point(xl, y0));
        }

        for (int i = 0; i < pts.Count; i++)
        {
            if (i == 0) ctx.DrawEllipse(Palette.T2Brush, null, pts[i], 3.5, 3.5);
            else ctx.DrawEllipse(Palette.BgCardBrush, Palette.Pen(Palette.AColor, 2), pts[i], 4, 4);
        }
        // 每段末端的温度（保温段不重复写）；最后一段写在点的左边，降温段写在线的下面
        foreach (var r in d.Rows)
        {
            if (r.Kind == CurveKind.Hold) continue;
            bool last = r.I == d.Rows.Count;
            Draw(ctx, HeatCurve.N(r.TempC) + " °C", X(r.EndMin) + (last ? -10 : 8), Y(r.TempC) + (r.Kind == CurveKind.Down ? 22 : -9), last ? 1 : -1,
                Palette.ABrush, Palette.AColor, bold: true, halo: true);
        }
        if (at == null)
            Draw(ctx, "当前 " + d.StartC.ToString("0.0", CultureInfo.InvariantCulture) + " °C", X(0) + 12, Y(d.StartC) + 5, -1, Palette.T2Brush, Palette.T2Color, halo: true);
        else
        {
            double xn = X(d.ElapsedMin);
            bool right = xn < W - R - 150;
            Draw(ctx, "现在 " + Fmt.Hm(d.StartAt.AddMinutes(d.ElapsedMin)), xn + (right ? 6 : -6), T + 13, right ? -1 : 1, Palette.T2Brush, Palette.T2Color, halo: true);
            if (d.PvC is double pv)
            {
                ctx.DrawEllipse(Palette.BgCardBrush, Palette.Pen(Palette.GDotColor, 2), new Point(xn, Y(pv)), 5, 5);
                Draw(ctx, "实测 " + d.PvText + " °C", xn + (right ? 9 : -9), Y(pv) + 18, right ? -1 : 1, Palette.GInkBrush, Palette.GInkColor, bold: true, halo: true);
            }
        }
    }

    private static StreamGeometry Line(IReadOnlyList<Point> pts)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(pts[0], false);
        for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i]);
        c.EndFigure(false);
        return g;
    }
}
