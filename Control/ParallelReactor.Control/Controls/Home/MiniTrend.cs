using System;
using Avalonia;
using Avalonia.Media;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Home;

/// <summary>
/// 报警弹窗的小趋势图（样稿 alarmPop 的 svg，宽度拉满、高 96 / 矮屏 72）：纵轴 0 … max(上限, 读数) × 1.12（上下各留 14 / 10）；
/// 上限红虚线（4 4，70 %）、设定灰点线（2 3，t-icon）、报警时刻红竖线（35 %）、实测品牌蓝 2 px、末端红点（4 px、白边 1.5）。没有文字。
/// </summary>
public sealed class MiniTrend : Avalonia.Controls.Control
{
    public static readonly StyledProperty<PopTrend?> DataProperty = AvaloniaProperty.Register<MiniTrend, PopTrend?>(nameof(Data));

    public PopTrend? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    static MiniTrend()
    {
        AffectsRender<MiniTrend>(DataProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MiniTrend>(true);
    }

    public override void Render(DrawingContext ctx)
    {
        var d = Data;
        double W = Bounds.Width, H = Bounds.Height;
        if (d == null || d.Count < 2 || W < 40 || H < 30) return;
        double vmax = d.Limit ?? 0;
        for (int i = 0; i < d.Count; i++) if (double.IsFinite(d.Points[i].V)) vmax = Math.Max(vmax, d.Points[i].V);
        vmax = vmax > 0 ? vmax * 1.12 : 1;
        double span = Math.Max(1, d.T1 - d.T0);
        double X(double t) => 4 + (t - d.T0) / span * (W - 8);
        double Y(double v) => H - 14 - v / vmax * (H - 24);
        if (d.Limit is double lim) ctx.DrawLine(Palette.Pen(Palette.WithAlpha(Palette.RColor, 0.7), 1, "4 4"), new Point(4, Y(lim)), new Point(W - 4, Y(lim)));
        if (d.Sp is double sp) ctx.DrawLine(Palette.Pen(Palette.TIconColor, 1, "2 3"), new Point(4, Y(sp)), new Point(W - 4, Y(sp)));
        if (d.AlarmT is double at && at > d.T0 && at < d.T1) ctx.DrawLine(Palette.Pen(Palette.WithAlpha(Palette.RColor, 0.35), 1), new Point(X(at), 6), new Point(X(at), H - 14));
        var g = new StreamGeometry();
        Point? last = null;
        using (var c = g.Open())
        {
            bool open = false;
            for (int i = 0; i < d.Count; i++)
            {
                var p = d.Points[i];
                if (!double.IsFinite(p.V)) { if (open) c.EndFigure(false); open = false; continue; }
                var pt = new Point(X(p.T), Y(p.V));
                if (!open) { c.BeginFigure(pt, false); open = true; } else c.LineTo(pt);
                last = pt;
            }
            if (open) c.EndFigure(false);
        }
        ctx.DrawGeometry(null, Palette.Pen(Palette.AColor, 2), g);
        if (last is { } lp) ctx.DrawEllipse(Palette.RBrush, Palette.Pen(Palette.BgCardColor, 1.5), lp, 4, 4);
    }
}
