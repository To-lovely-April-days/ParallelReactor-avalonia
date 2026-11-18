using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Pages.Curves;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Curves;

/// <summary>
/// 曲线页的一张图（融合版 F5，样稿 PAGES.curve.after 的 SVG 逐项照画）：一个量（温度 / 压力 / 气体消耗），单纵轴（不做双纵轴），几张图上下排列共用时间轴。
/// <list type="bullet">
/// <item>绘图区：左 56、右 16（≤ 4 路时 52，末端写 RVn）、上 22、下 8（最后一张 30，写时刻）。左上角「温度 (°C)」13/600 t-2。</item>
/// <item>纵轴：每个间隔一条横网格（ch-grid）+ 左侧刻度字（13，t-3，右对齐到 L − 8）；横轴：按时钟取整的竖网格（bg-side），最后一张写「13:15」。左边、底边两条轴线（ch-axis）。</item>
/// <item>压力图：报警上限红虚线（4 4，60 %）+ 右端「报警上限 3.50」（r-ink）。标记：竖虚线（3 3，70 %；报警类红、其余 t-3），第一张图上方画小旗 + 文字。</item>
/// <item>每路一条线（识别色 2 px，RV9–16 虚线 6 4）；设定值 = 识别色点线（2 3，80 %）。</item>
/// <item>光标：<see cref="CursorT"/>（ms）处一条竖线（t-1）。指针移动时发 <see cref="CursorMoved"/>（页面取整后再设回 <see cref="CursorT"/>，所有图同步）。</item>
/// </list>
/// 自绘文字经 <see cref="IAuditText"/> 报给截图审计。
/// </summary>
public sealed class CurveChart : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<CurvePanelData?> DataProperty = AvaloniaProperty.Register<CurveChart, CurvePanelData?>(nameof(Data));
    public static readonly StyledProperty<double?> CursorTProperty = AvaloniaProperty.Register<CurveChart, double?>(nameof(CursorT));

    public CurvePanelData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    /// <summary>光标的时刻（ms）；null = 不画。</summary>
    public double? CursorT { get => GetValue(CursorTProperty); set => SetValue(CursorTProperty, value); }

    /// <summary>指针在绘图区里移动（时刻 ms）/ 离开（null）。</summary>
    public event Action<double?>? CursorMoved;

    public const double L = 56, T = 22, Fs = 13;
    private readonly List<AuditTextRun> _audit = new();

    static CurveChart()
    {
        AffectsRender<CurveChart>(DataProperty, CursorTProperty);
        ClipToBoundsProperty.OverrideDefaultValue<CurveChart>(true);
    }

    public CurveChart()
    {
        PointerMoved += (_, e) => OnPointer(e.GetPosition(this).X);
        PointerExited += (_, _) => CursorMoved?.Invoke(null);
    }

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    /// <summary>右边距：≤ 4 路（末端写 RVn）52，否则 16。</summary>
    public static double RightOf(bool endLabels) => endLabels ? 52 : 16;

    /// <summary>下边距：最后一张 30（写时刻），其余 8。</summary>
    public static double BottomOf(bool last) => last ? 30 : 8;

    private void OnPointer(double px)
    {
        var d = Data;
        if (d == null) return;
        double iw = Bounds.Width - L - RightOf(d.EndLabels);
        if (iw <= 0 || px < L || px > L + iw) return;
        CursorMoved?.Invoke(d.T0 + (px - L) / iw * (d.T1 - d.T0));
    }

    private Typeface Face(FontWeight w) => new(TextElement.GetFontFamily(this), FontStyle.Normal, w);

    private FormattedText Text(string s, IBrush brush, FontWeight w = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face(w), Fs, brush);

    /// <summary>画一段字：<paramref name="anchor"/> -1 左 / 0 中 / 1 右；<paramref name="baseline"/> = SVG 的 y（基线）。</summary>
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
        if (d == null || W < 120 || H < 60) return;
        double R = RightOf(d.EndLabels), B = BottomOf(d.Last);
        double iw = W - L - R, ih = H - T - B, span = Math.Max(1, d.T1 - d.T0);
        var ax = d.Axis;
        double X(double t) => L + (t - d.T0) / span * iw;
        double Y(double v) => T + ih - (v - ax.Lo) / (ax.Hi - ax.Lo) * ih;

        Draw(ctx, d.Title + " (" + d.Unit + ")", L, 14, -1, Palette.T2Brush, Palette.T2Color, FontWeight.SemiBold);

        // 横网格 + 纵轴刻度
        var grid = Palette.Pen(Palette.ChGridColor, 1);
        int steps = (int)Math.Round((ax.Hi - ax.Lo) / ax.Step);
        for (int i = 0; i <= steps; i++)
        {
            double v = ax.Lo + ax.Step * i, y = Math.Round(Y(v)) + 0.5;
            ctx.DrawLine(grid, new Point(L, y), new Point(L + iw, y));
            Draw(ctx, v.ToString(d.AxisDec > 0 ? "0." + new string('0', d.AxisDec) : "0", CultureInfo.InvariantCulture),
                L - 8, Y(v) + 4, 1, Palette.T3Brush, Palette.T3Color);
        }
        // 竖网格（按时钟取整）+ 最后一张写时刻
        var vgrid = Palette.Pen(Palette.BgSideColor, 1);
        var start = d.Start;
        // 窗口起点离整刻度不到 30 s（「1 h」= 现在 − 60 min，现在是 14:30:07）时那个刻度算在起点上（同样稿：窗口按整分钟，起点就有刻度）
        var first = start.Date.AddMinutes(Math.Floor((start - start.Date).TotalMinutes / d.TickMinutes) * d.TickMinutes);
        if ((start - first).TotalSeconds >= 30) first = first.AddMinutes(d.TickMinutes);
        for (var t = first; ChartTime.Ms(t) <= d.T1 + 1; t = t.AddMinutes(d.TickMinutes))
        {
            double xt = Math.Max(L, X(ChartTime.Ms(t)));
            double x = Math.Round(xt) + 0.5;
            ctx.DrawLine(vgrid, new Point(x, T), new Point(x, T + ih));
            if (d.Last) Draw(ctx, t.ToString("HH:mm", CultureInfo.InvariantCulture), xt, T + ih + 19, 0, Palette.T3Brush, Palette.T3Color);
        }

        // 报警上限
        if (d.Limit is double lim && lim >= ax.Lo && lim <= ax.Hi)
        {
            double y = Math.Round(Y(lim)) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.WithAlpha(Palette.ChLimitColor, 0.6), 1, "4 4"), new Point(L, y), new Point(L + iw, y));
            Draw(ctx, "报警上限 " + lim.ToString("F" + d.Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), L + iw - 4, Y(lim) - 5, 1, Palette.RInkBrush, Palette.RInkColor);
        }
        // 标记
        foreach (var m in d.Marks)
        {
            if (m.T < d.T0 || m.T > d.T1) continue;
            double x = Math.Round(X(m.T)) + 0.5;
            var c = m.Err ? Palette.ChLimitColor : Palette.T3Color;
            ctx.DrawLine(Palette.Pen(Palette.WithAlpha(c, 0.7), 1, "3 3"), new Point(x, T), new Point(x, T + ih));
            if (d.First)
            {
                // 小旗（样稿 path M0 0V-12H8L6 -9L8 -6H0，放在 (x, T − 4)）
                double fx = X(m.T), fy = T - 4;
                var g = new StreamGeometry();
                using (var sc = g.Open())
                {
                    sc.BeginFigure(new Point(fx, fy), true);
                    sc.LineTo(new Point(fx, fy - 12));
                    sc.LineTo(new Point(fx + 8, fy - 12));
                    sc.LineTo(new Point(fx + 6, fy - 9));
                    sc.LineTo(new Point(fx + 8, fy - 6));
                    sc.LineTo(new Point(fx, fy - 6));
                    sc.EndFigure(true);
                }
                ctx.DrawGeometry(new SolidColorBrush(c), null, g);
                Draw(ctx, m.Text, fx + 11, fy - 3, -1, Palette.T2Brush, Palette.T2Color);
            }
        }
        // 轴线
        var axis = Palette.Pen(Palette.ChAxisColor, 1);
        ctx.DrawLine(axis, new Point(L + 0.5, T), new Point(L + 0.5, T + ih));
        ctx.DrawLine(axis, new Point(L, Math.Round(T + ih) + 0.5), new Point(L + iw, Math.Round(T + ih) + 0.5));

        using (ctx.PushClip(new Rect(L, T - 2, iw + R, ih + 4)))
        {
            // 设定值（识别色点线）
            foreach (var (c, v) in d.SpLines)
            {
                if (v < ax.Lo || v > ax.Hi) continue;
                double y = Math.Round(Y(v)) + 0.5;
                ctx.DrawLine(Palette.Pen(Palette.WithAlpha(c, 0.8), 1, "2 3"), new Point(L, y), new Point(L + iw, y));
            }
            // 曲线
            foreach (var ln in d.Lines)
            {
                var g = Path(ln, X, Y);
                if (g == null) continue;
                ctx.DrawGeometry(null, Palette.Pen(ln.Color, 2, ln.Dashed ? "3 2" : null, PenLineCap.Flat), g);
            }
        }
        // 末端标签（≤ 4 路）
        if (d.EndLabels)
            foreach (var ln in d.Lines)
                if (LastOk(ln) is { } p)
                    Draw(ctx, "RV" + ln.N.ToString(CultureInfo.InvariantCulture), X(p.T) + 6, Y(p.V) + 4, -1, Palette.T2Brush, Palette.T2Color, FontWeight.SemiBold);
        if (d.Lines.Count == 0 && d.Empty is { } empty)
            Draw(ctx, empty, L + iw / 2, T + ih / 2, 0, Palette.T3Brush, Palette.T3Color);

        // 光标
        if (CursorT is double ct && ct >= d.T0 && ct <= d.T1)
        {
            double x = Math.Round(X(ct)) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.T1Color, 1), new Point(x, T), new Point(x, T + ih));
        }
    }

    private static ChartPoint? LastOk(CurveLine ln)
    {
        for (int i = ln.Count - 1; i >= 0; i--) if (double.IsFinite(ln.Points[i].V)) return ln.Points[i];
        return null;
    }

    /// <summary>折线（NaN 处断开）。</summary>
    private static StreamGeometry? Path(CurveLine ln, Func<double, double> X, Func<double, double> Y)
    {
        if (ln.Count == 0) return null;
        var g = new StreamGeometry();
        bool any = false;
        using (var c = g.Open())
        {
            bool open = false;
            for (int i = 0; i < ln.Count; i++)
            {
                var p = ln.Points[i];
                if (!double.IsFinite(p.V)) { if (open) c.EndFigure(false); open = false; continue; }
                var pt = new Point(X(p.T), Y(p.V));
                if (!open) { c.BeginFigure(pt, false); open = true; any = true; }
                else c.LineTo(pt);
            }
            if (open) c.EndFigure(false);
        }
        return any ? g : null;
    }
}
