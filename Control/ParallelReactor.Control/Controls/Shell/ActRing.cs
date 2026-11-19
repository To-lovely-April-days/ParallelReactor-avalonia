using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using ParallelReactor.Control.Core.Activity;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 设备活动的分段圆环（v0.8.1，样稿 act2.js ax2Ring）：从 12 点钟顺时针，段长按权重、段间留 环宽 + 4 的缝（只有一段时不留），圆头。
/// 已完成品牌蓝（标定的第一遍浅品牌蓝）、当前 = 浅蓝底 + 蓝色进度（等人操作 = 浅琥珀底 + 琥珀）、未开始灰；结果段：通过绿、不通过红、未测琥珀、中止灰。
/// 直径 = 控件的宽（<see cref="Size"/>），环宽 <see cref="Stroke"/>。
/// </summary>
public sealed class ActRing : Avalonia.Controls.Control
{
    public static readonly StyledProperty<IReadOnlyList<RingSeg>?> SegsProperty = AvaloniaProperty.Register<ActRing, IReadOnlyList<RingSeg>?>(nameof(Segs));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<ActRing, double>(nameof(Size), 128);
    public static readonly StyledProperty<double> StrokeProperty = AvaloniaProperty.Register<ActRing, double>(nameof(Stroke), 9);

    static ActRing()
    {
        AffectsRender<ActRing>(SegsProperty, StrokeProperty);
        AffectsMeasure<ActRing>(SizeProperty);
    }

    public IReadOnlyList<RingSeg>? Segs { get => GetValue(SegsProperty); set => SetValue(SegsProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    private static Color Base(RingSegKind k) => k switch
    {
        RingSegKind.Done => Palette.AColor,
        RingSegKind.DoneSoft => Palette.AMuteColor,
        RingSegKind.Cur => Palette.AxCurColor,
        RingSegKind.CurAmb => Palette.AxCurAmbColor,
        RingSegKind.Ok => Palette.GDotColor,
        RingSegKind.No => Palette.RColor,
        RingSegKind.Amb => Palette.AxAmbColor,
        RingSegKind.Ab => Palette.TIconColor,
        _ => Palette.AxTrackColor,
    };

    public override void Render(DrawingContext ctx)
    {
        var segs = Segs;
        if (segs is null || segs.Count == 0) return;
        double sw = Stroke, d = Size, r = (d - sw) / 2, c = 2 * Math.PI * r;
        var center = new Point(d / 2, d / 2);
        double tot = 0;
        foreach (var s in segs) tot += Math.Max(0, s.Weight);
        if (tot <= 0) return;
        double gap = segs.Count > 1 ? sw + 4 : 0, off = 0;
        foreach (var s in segs)
        {
            double len = Math.Max(0, s.Weight) / tot * c;
            double l = Math.Max(len - gap, 0.01), o = off + gap / 2;
            Arc(ctx, center, r, o / c, l / c, Base(s.Kind), sw);
            if (s.Kind is RingSegKind.Cur or RingSegKind.CurAmb && s.Fill > 0)
                Arc(ctx, center, r, o / c, l * Math.Min(1, s.Fill) / c, s.Kind == RingSegKind.CurAmb ? Palette.AxAmbColor : Palette.AColor, sw);
            off += len;
        }
    }

    /// <summary>从 12 点钟顺时针、起点 <paramref name="f0"/>（整圈 = 1）画长 <paramref name="f"/> 的一段圆弧（圆头）。</summary>
    private static void Arc(DrawingContext ctx, Point c, double r, double f0, double f, Color col, double sw)
    {
        var pen = Palette.Pen(col, sw, cap: PenLineCap.Round);
        if (f >= 0.9999) { ctx.DrawEllipse(null, Palette.Pen(col, sw), c, r, r); return; }
        double a0 = -Math.PI / 2 + f0 * 2 * Math.PI, a1 = a0 + f * 2 * Math.PI;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
            g.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
