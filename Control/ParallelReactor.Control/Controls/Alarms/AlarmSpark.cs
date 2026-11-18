using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ParallelReactor.Control.Pages.Alarms;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Alarms;

/// <summary>
/// 报警页详情里「报警前后」的小趋势（融合版 F5，样稿 alSpark 的 SVG 逐项照画，高 150）：绘图区左 40 右 10 上 18 下 22；
/// 左上角「RV4 压力 (MPa) · 报警前后」（单位按设置）13/600 t-2；纵轴 0 / 一半 / 最高（= max(读数, 上限) × 1.1），横网格 ch-grid，刻度字 13 t-3（压力 MPa / bar 1 位小数、psi 整数，<see cref="AlarmSparkData.Dec"/>）；
/// 上限红虚线（4 4，60 %）；曲线 = 通道识别色 2 px；报警时刻红竖虚线（3 3）；底下三个时刻（左对齐 / 居中 / 右对齐）。
/// </summary>
public sealed class AlarmSpark : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<AlarmSparkData?> DataProperty = AvaloniaProperty.Register<AlarmSpark, AlarmSparkData?>(nameof(Data));

    public AlarmSparkData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private const double L = 40, R = 10, T = 18, B = 22, Fs = 13;
    private readonly List<AuditTextRun> _audit = new();

    static AlarmSpark()
    {
        AffectsRender<AlarmSpark>(DataProperty);
        ClipToBoundsProperty.OverrideDefaultValue<AlarmSpark>(true);
    }

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    private void Draw(DrawingContext ctx, string s, double x, double baseline, int anchor, IBrush b, Color c, FontWeight w = FontWeight.Normal)
    {
        var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, w), Fs, b);
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
        double iw = W - L - R, ih = H - T - B, span = Math.Max(1, d.T1 - d.T0);
        double hi = d.Limit ?? 0;
        for (int i = 0; i < d.Count; i++) if (double.IsFinite(d.Points[i].V)) hi = Math.Max(hi, d.Points[i].V);
        hi = hi > 0 ? hi * 1.1 : 1;
        double X(double t) => L + (t - d.T0) / span * iw;
        double Y(double v) => T + ih - v / hi * ih;

        Draw(ctx, d.Title, L, 12, -1, Palette.T2Brush, Palette.T2Color, FontWeight.SemiBold);
        var grid = Palette.Pen(Palette.ChGridColor, 1);
        foreach (var v in new[] { 0, hi / 2, hi })
        {
            double y = Math.Round(Y(v)) + 0.5;
            ctx.DrawLine(grid, new Point(L, y), new Point(L + iw, y));
            Draw(ctx, v.ToString(d.Dec > 0 ? "0." + new string('0', d.Dec) : "0", CultureInfo.InvariantCulture), L - 6, Y(v) + 4, 1, Palette.T3Brush, Palette.T3Color);
        }
        if (d.Limit is double lim)
        {
            double y = Math.Round(Y(lim)) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.WithAlpha(Palette.ChLimitColor, 0.6), 1, "4 4"), new Point(L, y), new Point(L + iw, y));
        }
        var g = new StreamGeometry();
        bool any = false;
        using (var c = g.Open())
        {
            bool open = false;
            for (int i = 0; i < d.Count; i++)
            {
                var p = d.Points[i];
                if (!double.IsFinite(p.V)) { if (open) c.EndFigure(false); open = false; continue; }
                var pt = new Point(X(p.T), Y(p.V));
                if (!open) { c.BeginFigure(pt, false); open = true; any = true; } else c.LineTo(pt);
            }
            if (open) c.EndFigure(false);
        }
        if (any) ctx.DrawGeometry(null, Palette.Pen(d.Color, 2), g);
        if (d.AlarmT >= d.T0 && d.AlarmT <= d.T1)
        {
            double x = Math.Round(X(d.AlarmT)) + 0.5;
            ctx.DrawLine(Palette.Pen(Palette.WithAlpha(Palette.ChLimitColor, 0.75), 1, "3 3"), new Point(x, T), new Point(x, T + ih));
        }
        if (d.Ticks.Count == 3)
        {
            Draw(ctx, d.Ticks[0], L, H - 4, -1, Palette.T3Brush, Palette.T3Color);
            Draw(ctx, d.Ticks[1], L + iw / 2, H - 4, 0, Palette.T3Brush, Palette.T3Color);
            Draw(ctx, d.Ticks[2], L + iw, H - 4, 1, Palette.T3Brush, Palette.T3Color);
        }
    }
}
