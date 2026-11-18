using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ParallelReactor.Control.Pages.Settings.Pid;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Settings;

/// <summary>
/// 自整定监视的曲线（样稿 pidChart 的 SVG，viewBox 560 × 292 逐项照画，宽度跟着面板、高度按 292 / 560 的比例（窄时横轴刻度至少离输出图底线 17 px）；字号固定 13（样稿 12；本机最小字号 13），不跟着缩小）：
/// <list type="bullet">
/// <item>上图「温度」：各路 PV（识别色 1.8 px，末端 3.5 px 圆点白边）+ 整定温度虚线（品牌蓝 1.2 px，虚线 5 4，右上写「整定温度 150 °C」）；
///   纵轴每 50 °C 一格，从 ⌊(SP − 120) / 50⌋·50（≥ 0）到 ⌈(SP + 20) / 50⌉·50，横线 ch-grid。</item>
/// <item>下图「加热输出 %」（不共用纵轴，两张图上下叠放）：各路输出 1.2 px、75 % 不透明（几路时按 0.92 − 0.06·序号 压低一点，方波不叠在一起）；0 / 100 两个刻度，底线 ch-axis。</item>
/// <item>横轴 = 距整定开始的分钟，每 12 min 一个刻度（「0 min」…）；整定中画「现在」竖虚线（t-dis，2 3）+「现在」。</item>
/// </list>
/// 自绘文字经 <see cref="IAuditText"/> 报给截图审计。
/// </summary>
public sealed class PidChart : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<PidChartData?> DataProperty = AvaloniaProperty.Register<PidChart, PidChartData?>(nameof(Data));

    public PidChartData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>样稿 viewBox 的尺寸（宽 560，高 H1 200 + H2 84 + 8）。</summary>
    public const double VbW = 560, VbH = 292;
    private const double L = 46, R = 14, T = 10, H1 = 200, H2 = 84, Fs = 13;
    private readonly List<AuditTextRun> _audit = new();

    static PidChart()
    {
        AffectsRender<PidChart>(DataProperty);
        AffectsMeasure<PidChart>(DataProperty);
    }

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    /// <summary>高度按样稿的比例（宽 × 292 / 560）。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) ? availableSize.Width : VbW;
        double k = w / VbW;
        // 窄的时候（1366：宽 416）横轴的「0 min」和输出图的「0」挨得太近：至少留出一行 13 px 字的距离
        return new Size(w, Math.Round(Math.Max(VbH * k, (H1 + H2 - 16) * k + 21)));
    }

    /// <summary>温度轴的范围（样稿 lo / hi）。</summary>
    public static (double Lo, double Hi) TempAxis(double sp) =>
        (Math.Max(0, Math.Floor((sp - 120) / 50) * 50), Math.Ceiling((sp + 20) / 50) * 50);

    /// <summary>横轴刻度（每 12 min，到总长为止）。</summary>
    public static IEnumerable<double> XTicks(double total)
    {
        double step = total > 60 ? 24 : 12;
        for (double m = 0; m <= total + 1e-9; m += step) yield return m;
    }

    private Typeface Face => new(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Normal);

    private void Draw(DrawingContext ctx, string s, double x, double baseline, int anchor, IBrush brush, Color c)
    {
        var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, Fs, brush);
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
        if (d == null || W < 200 || H < 120) return;
        // 纵向按高度换算样稿的坐标（文字不缩放）；横向绘图区 = 宽 − 左 46 − 右 14
        double k = W / VbW;
        double Yv(double vb) => vb * k;
        double iw = W - L - R, total = Math.Max(1, d.Total);
        double X(double m) => L + m / total * iw;
        var (lo, hi) = TempAxis(d.Sp);
        double Y1(double v) => Yv(T) + (1 - (Math.Max(lo, v) - lo) / (hi - lo)) * (Yv(H1 - 22) - Yv(T));
        double Y2(double v) => Yv(H1 + 14) + (1 - v / 100) * (Yv(H1 + H2 - 16) - Yv(H1 + 14));

        var grid = Palette.Pen(Palette.ChGridColor, 1);
        for (double v = lo; v <= hi + 1e-9; v += 50)
        {
            double y = Math.Round(Y1(v)) + 0.5;
            ctx.DrawLine(grid, new Point(L, y), new Point(W - R, y));
            Draw(ctx, v.ToString("0", CultureInfo.InvariantCulture), L - 6, y + 4, 1, Palette.T3Brush, Palette.T3Color);
        }
        double ys = Math.Round(Y1(d.Sp)) + 0.5;
        ctx.DrawLine(Palette.Pen(Palette.AColor, 1.2, "5 4"), new Point(L, ys), new Point(W - R, ys));
        Draw(ctx, "整定温度 " + Core.Pid.PidTexts.Num(d.Sp) + " °C", W - R, ys - 5, 1, Palette.ABrush, Palette.AColor);

        foreach (var m in XTicks(total))
            Draw(ctx, m.ToString("0", CultureInfo.InvariantCulture) + " min", X(m), Math.Max(H - 4, Y2(0) + 17), 0, Palette.T3Brush, Palette.T3Color);
        Draw(ctx, "加热输出 %", L, Yv(H1) + 10, -1, Palette.T2Brush, Palette.T2Color);
        double y0 = Math.Round(Y2(0)) + 0.5;
        ctx.DrawLine(Palette.Pen(Palette.ChAxisColor, 1), new Point(L, y0), new Point(W - R, y0));
        foreach (var v in new[] { 0.0, 100.0 })
            Draw(ctx, v.ToString("0", CultureInfo.InvariantCulture), L - 6, Y2(v) + 4, 1, Palette.T3Brush, Palette.T3Color);

        using (ctx.PushClip(new Rect(L - 5, 0, iw + 10, H)))
        {
            for (int li = 0; li < d.Lines.Count; li++)
            {
                var ln = d.Lines[li];
                double scale = 0.92 - li * 0.06;
                if (Path(ln.M, ln.T, X, Y1) is { } pv) ctx.DrawGeometry(null, Palette.Pen(ln.Color, 1.8, null, PenLineCap.Round), pv);
                if (Path(ln.M, ln.O, X, v => Y2(v * scale)) is { } po) ctx.DrawGeometry(null, Palette.Pen(Palette.WithAlpha(ln.Color, .75), 1.2), po);
                for (int i = ln.M.Length - 1; i >= 0; i--)
                {
                    if (!double.IsFinite(ln.T[i])) continue;
                    ctx.DrawEllipse(new SolidColorBrush(ln.Color), Palette.Pen(Palette.BgCardColor, 1.5), new Point(X(ln.M[i]), Y1(ln.T[i])), 3.5, 3.5);
                    break;
                }
            }
        }
        if (d.Now is double now)
        {
            double x = Math.Round(X(Math.Min(now, total))) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.TDisColor, 1, "2 3"), new Point(x, Yv(T)), new Point(x, Yv(H1 + H2 - 14)));
            Draw(ctx, "现在", x + 4, Yv(T) + 10, -1, Palette.T3Brush, Palette.T3Color);
        }
    }

    /// <summary>一条折线（NaN 断开）；没有点为 null。</summary>
    private static StreamGeometry? Path(double[] m, double[] v, Func<double, double> X, Func<double, double> Y)
    {
        var g = new StreamGeometry();
        bool any = false;
        using (var c = g.Open())
        {
            bool open = false;
            for (int i = 0; i < m.Length; i++)
            {
                if (!double.IsFinite(v[i])) { if (open) c.EndFigure(false); open = false; continue; }
                var p = new Point(X(m[i]), Y(v[i]));
                if (!open) { c.BeginFigure(p, false); open = true; any = true; }
                else c.LineTo(p);
            }
            if (open) c.EndFigure(false);
        }
        return any ? g : null;
    }
}
