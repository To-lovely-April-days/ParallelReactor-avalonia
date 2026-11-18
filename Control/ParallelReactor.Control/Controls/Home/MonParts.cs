using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Home;

/// <summary>管子的样子：竖管 / 横管（3 px，pipe 色）、总管（5 px、圆角 3，pipe-hdr 色）。</summary>
public enum PipeKind { V, H, Header }

/// <summary>
/// 气路图的一段管子（样稿 i.ln / i.hl / i.hdr）：平时 pipe（总管 pipe-hdr）实色；有气流（.gf）时浅绿底 g-3（总管 g-4）+ 运行绿 g-dot 的虚线段
/// （竖管 / 横管 7 on 7 off，总管 8 / 8）。虚线从哪一端开始同样稿的 repeating-linear-gradient 方向：竖管默认从下往上（to top），
/// <see cref="FromStart"/> = 从上往下（汇流管 to bottom）；横管从左往右。截图冻结，不做流动动画。
/// </summary>
public sealed class PipeLine : Avalonia.Controls.Control
{
    public static readonly StyledProperty<PipeKind> KindProperty = AvaloniaProperty.Register<PipeLine, PipeKind>(nameof(Kind));
    public static readonly StyledProperty<bool> FlowProperty = AvaloniaProperty.Register<PipeLine, bool>(nameof(Flow));
    public static readonly StyledProperty<bool> FromStartProperty = AvaloniaProperty.Register<PipeLine, bool>(nameof(FromStart));
    public static readonly StyledProperty<double> DashProperty = AvaloniaProperty.Register<PipeLine, double>(nameof(Dash), double.NaN);

    static PipeLine() => AffectsRender<PipeLine>(KindProperty, FlowProperty, FromStartProperty, DashProperty);

    /// <summary>虚线段长（NaN = 竖管 / 横管 7、总管 8；图例 5）。</summary>
    public double Dash { get => GetValue(DashProperty); set => SetValue(DashProperty, value); }

    public PipeKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool Flow { get => GetValue(FlowProperty); set => SetValue(FlowProperty, value); }
    public bool FromStart { get => GetValue(FromStartProperty); set => SetValue(FromStartProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width <= 0 || r.Height <= 0) return;
        bool hdr = Kind == PipeKind.Header;
        double rad = hdr ? Math.Min(3, r.Height / 2) : 0;
        if (!Flow)
        {
            ctx.DrawRectangle(hdr ? Palette.PipeHdrBrush : Palette.PipeBrush, null, new RoundedRect(r, rad));
            return;
        }
        ctx.DrawRectangle(hdr ? Palette.G4Brush : Palette.G3Brush, null, new RoundedRect(r, rad));
        double on = double.IsFinite(Dash) ? Dash : hdr ? 8 : 7, period = double.IsFinite(Dash) ? Dash + 4 : 2 * on;
        using var _ = ctx.PushGeometryClip(new RectangleGeometry(r));
        var dash = Palette.GDotBrush;
        if (Kind == PipeKind.V)
        {
            if (FromStart) for (double y = 0; y < r.Height; y += period) ctx.FillRectangle(dash, new Rect(0, y, r.Width, Math.Min(on, r.Height - y)));
            else for (double y = r.Height; y > 0; y -= period) ctx.FillRectangle(dash, new Rect(0, Math.Max(0, y - on), r.Width, Math.Min(on, y)));
        }
        else
            for (double x = 0; x < r.Width; x += period) ctx.FillRectangle(dash, new Rect(x, 0, Math.Min(on, r.Width - x), r.Height));
    }
}

/// <summary>
/// 阀门符号（样稿 VSYM，20 网格）：竖向 = 上下两个对顶三角（进气阀 SV、排空阀），横向 = 左右两个（气源阀）。关 = 白底 sym 色描边 1.4，开 = 运行绿实心。
/// 边长 <see cref="Size"/>（默认 20；图例 16，紧凑 16）。
/// </summary>
public sealed class ValveGlyph : Avalonia.Controls.Control
{
    public static readonly StyledProperty<bool> OpenProperty = AvaloniaProperty.Register<ValveGlyph, bool>(nameof(Open));
    public static readonly StyledProperty<bool> HorizontalProperty = AvaloniaProperty.Register<ValveGlyph, bool>(nameof(Horizontal));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<ValveGlyph, double>(nameof(Size), 20);

    static ValveGlyph()
    {
        AffectsRender<ValveGlyph>(OpenProperty, HorizontalProperty, SizeProperty);
        AffectsMeasure<ValveGlyph>(SizeProperty);
    }

    public bool Open { get => GetValue(OpenProperty); set => SetValue(OpenProperty, value); }
    public bool Horizontal { get => GetValue(HorizontalProperty); set => SetValue(HorizontalProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private static readonly Geometry V = Geometry.Parse("M5 3.5H15L10 10Z M5 16.5H15L10 10Z");
    private static readonly Geometry H = Geometry.Parse("M3.5 5V15L10 10Z M16.5 5V15L10 10Z");

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext ctx)
    {
        double s = Size / 20;
        using var _ = ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2));
        var c = Open ? Palette.GDotColor : Palette.SymColor;
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(c), 1.4, null, PenLineCap.Flat, PenLineJoin.Round);
        ctx.DrawGeometry(Open ? Palette.GDotBrush : Palette.BgCardBrush, pen, Horizontal ? H : V);
    }
}

/// <summary>单向阀 CV（样稿 svg.cv，18 网格）：白底圆 sym 描边 1.3 + 实心向上三角。</summary>
public sealed class CheckValveGlyph : Avalonia.Controls.Control
{
    private static readonly Geometry Tri = Geometry.Parse("M5.6 11.4 9 5.6 12.4 11.4Z");
    private static readonly ImmutablePen Ring = new(new ImmutableSolidColorBrush(Palette.SymColor), 1.3);

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => new(18, 18);

    public override void Render(DrawingContext ctx)
    {
        using var _ = ctx.PushTransform(Matrix.CreateTranslation((Bounds.Width - 18) / 2, (Bounds.Height - 18) / 2));
        ctx.DrawEllipse(Palette.BgCardBrush, Ring, new Point(9, 9), 7.5, 7.5);
        ctx.DrawGeometry(Palette.SymBrush, null, Tri);
    }
}

/// <summary>
/// 等宽的步骤小条（样稿卡片 .vpg：每步一段 flex 1、高 4、段间 2、圆角 2；棒图 .ss：每段 14 × 5、段间 2、圆角 1）：
/// 已完成 a-mute、当前 = 轨道 + 品牌蓝进度（至少 8 %，报警红）、未到 = 轨道（line-soft）。段宽 <see cref="SegWidth"/> 为 NaN 时平分宽度。
/// </summary>
public sealed class StepStrip : Avalonia.Controls.Control
{
    public static readonly StyledProperty<IReadOnlyList<StepSeg>?> SegsProperty = AvaloniaProperty.Register<StepStrip, IReadOnlyList<StepSeg>?>(nameof(Segs));
    public static readonly StyledProperty<double> SegWidthProperty = AvaloniaProperty.Register<StepStrip, double>(nameof(SegWidth), double.NaN);
    public static readonly StyledProperty<double> SegHeightProperty = AvaloniaProperty.Register<StepStrip, double>(nameof(SegHeight), 4);
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<StepStrip, double>(nameof(Radius), 2);

    static StepStrip()
    {
        AffectsRender<StepStrip>(SegsProperty, SegWidthProperty, SegHeightProperty, RadiusProperty);
        AffectsMeasure<StepStrip>(SegsProperty, SegWidthProperty, SegHeightProperty);
    }

    public IReadOnlyList<StepSeg>? Segs { get => GetValue(SegsProperty); set => SetValue(SegsProperty, value); }
    public double SegWidth { get => GetValue(SegWidthProperty); set => SetValue(SegWidthProperty, value); }
    public double SegHeight { get => GetValue(SegHeightProperty); set => SetValue(SegHeightProperty, value); }
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    private const double Gap = 2;

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        int n = Segs?.Count ?? 0;
        double w = double.IsFinite(SegWidth) ? n * SegWidth + Math.Max(0, n - 1) * Gap : 0;
        return new Avalonia.Size(w, SegHeight);
    }

    public override void Render(DrawingContext ctx)
    {
        var s = Segs;
        if (s == null || s.Count == 0) return;
        int n = s.Count;
        double sw = double.IsFinite(SegWidth) ? SegWidth : Math.Max(0, (Bounds.Width - Gap * (n - 1)) / n);
        double h = SegHeight, y = Math.Round((Bounds.Height - h) / 2), x = 0;
        foreach (var g in s)
        {
            var r = new Rect(x, y, sw, h);
            ctx.DrawRectangle(g.State == SegmentState.Done ? Palette.AMuteBrush : Palette.LineSoftBrush, null, new RoundedRect(r, Radius));
            if (g.State == SegmentState.Cur)
            {
                using (ctx.PushGeometryClip(new RectangleGeometry(r)))
                    ctx.DrawRectangle(g.Err ? Palette.RBrush : Palette.ABrush, null, new RoundedRect(new Rect(x, y, sw * Math.Max(0.08, g.Fill), h), Radius));
            }
            x += sw + Gap;
        }
    }
}

/// <summary>
/// 棒图的一对竖条（样稿 barView .bars：ISA-101 风格）：左 = 温度（条高 = 温度 / 最高温，填 ch-t 浅色）、右 = 压力（压力 / 量程，填品牌蓝浅色；
/// 报警时整根变红）；条底 bg-sunken、细边 v-tb-line、圆角 2。黑横线 + 向左的小三角 = 目标（16 路不画三角），红虚线 = 压力报警上限。
/// 条宽 <see cref="BarWidth"/>（8 路 20、4 路 30、16 路 11）、两条间距 <see cref="BarGap"/>（14 / 5）；高度 = 控件高。
/// </summary>
public sealed class BarPair : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> TFracProperty = AvaloniaProperty.Register<BarPair, double>(nameof(TFrac));
    public static readonly StyledProperty<double> SpFracProperty = AvaloniaProperty.Register<BarPair, double>(nameof(SpFrac), double.NaN);
    public static readonly StyledProperty<double> PFracProperty = AvaloniaProperty.Register<BarPair, double>(nameof(PFrac));
    public static readonly StyledProperty<double> PspFracProperty = AvaloniaProperty.Register<BarPair, double>(nameof(PspFrac), double.NaN);
    public static readonly StyledProperty<double> LimFracProperty = AvaloniaProperty.Register<BarPair, double>(nameof(LimFrac), double.NaN);
    public static readonly StyledProperty<bool> HotProperty = AvaloniaProperty.Register<BarPair, bool>(nameof(Hot));
    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<BarPair, double>(nameof(BarWidth), 20);
    public static readonly StyledProperty<double> BarGapProperty = AvaloniaProperty.Register<BarPair, double>(nameof(BarGap), 14);
    public static readonly StyledProperty<bool> ArrowsProperty = AvaloniaProperty.Register<BarPair, bool>(nameof(Arrows), true);

    static BarPair()
    {
        AffectsRender<BarPair>(TFracProperty, SpFracProperty, PFracProperty, PspFracProperty, LimFracProperty, HotProperty, BarWidthProperty, BarGapProperty, ArrowsProperty);
        AffectsMeasure<BarPair>(BarWidthProperty, BarGapProperty);
    }

    public double TFrac { get => GetValue(TFracProperty); set => SetValue(TFracProperty, value); }
    public double SpFrac { get => GetValue(SpFracProperty); set => SetValue(SpFracProperty, value); }
    public double PFrac { get => GetValue(PFracProperty); set => SetValue(PFracProperty, value); }
    public double PspFrac { get => GetValue(PspFracProperty); set => SetValue(PspFracProperty, value); }
    public double LimFrac { get => GetValue(LimFracProperty); set => SetValue(LimFracProperty, value); }
    public bool Hot { get => GetValue(HotProperty); set => SetValue(HotProperty, value); }
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }
    /// <summary>目标线右端画小三角（16 路不画，样稿 .fb.n16 .bar em::after{display:none}）。</summary>
    public bool Arrows { get => GetValue(ArrowsProperty); set => SetValue(ArrowsProperty, value); }

    private static readonly IImmutableBrush TFill = new ImmutableSolidColorBrush(Palette.ChTColor, 0.55);
    private static readonly IImmutableBrush PFill = new ImmutableSolidColorBrush(Palette.AColor, 0.5);
    private static readonly ImmutablePen Edge = new(new ImmutableSolidColorBrush(Palette.VTbLineColor), 1);
    private static readonly ImmutablePen LimPen = new(new ImmutableSolidColorBrush(Palette.RColor), 2, new ImmutableDashStyle(new double[] { 2, 1.5 }, 0));

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) =>
        new(2 * BarWidth + BarGap, double.IsFinite(availableSize.Height) ? availableSize.Height : 170);

    /// <summary>条里 v（0–1）对应的 y（相对条的内框：上下各 1 px 边）。</summary>
    public static double YOf(double v, double h) => 1 + (h - 2) * (1 - Math.Clamp(v, 0, 1));

    public override void Render(DrawingContext ctx)
    {
        double h = Bounds.Height, bw = BarWidth;
        if (!(h > 4)) return;
        double x0 = (Bounds.Width - (2 * bw + BarGap)) / 2;
        DrawBar(ctx, x0, bw, h, TFrac, Palette.ChTColor, TFill, SpFrac, double.NaN);
        DrawBar(ctx, x0 + bw + BarGap, bw, h, PFrac, Palette.AColor, Hot ? Palette.RBrush : PFill, PspFrac, LimFrac);
    }

    private void DrawBar(DrawingContext ctx, double x, double bw, double h, double v, Color _, IBrush fill, double sp, double lim)
    {
        var box = new Rect(x + 0.5, 0.5, bw - 1, h - 1);
        ctx.DrawRectangle(Palette.LineFaintBrush, null, new RoundedRect(new Rect(x, 0, bw, h), 2));
        if (double.IsFinite(v) && v > 0)
        {
            double y = YOf(v, h);
            ctx.FillRectangle(fill, new Rect(x + 1, y, bw - 2, h - 1 - y));
        }
        ctx.DrawRectangle(null, Edge, new RoundedRect(box, 2));
        if (double.IsFinite(lim))
        {
            double y = YOf(lim, h) - 1;
            ctx.DrawLine(LimPen, new Point(x - 3, y), new Point(x + bw + 3, y));
        }
        if (double.IsFinite(sp))
        {
            double y = YOf(sp, h), ext = Arrows ? 5 : 3;
            ctx.FillRectangle(Palette.T1Brush, new Rect(x - ext, y - 1, bw + 2 * ext, 2));
            if (Arrows)
            {
                double xr = x + bw + ext;
                var tri = new StreamGeometry();
                using (var c = tri.Open())
                {
                    c.BeginFigure(new Point(xr, y), true);
                    c.LineTo(new Point(xr + 5, y - 5));
                    c.LineTo(new Point(xr + 5, y + 5));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(Palette.T1Brush, null, tri);
            }
        }
    }
}

/// <summary>
/// 通道小块上的状态点 / 报警标记（样稿 .dot / ind）：没有报警时 7 px 圆点（运行 g-dot、报警 r、其余 n-dot）；
/// 有报警时 16 px 的实心图标（报警 f-err-f r-btn 色、提醒 f-warn-f 琥珀）。
/// </summary>
public sealed class TileMarkView : Avalonia.Controls.Control
{
    public static readonly StyledProperty<RunDot> DotProperty = AvaloniaProperty.Register<TileMarkView, RunDot>(nameof(Dot));
    public static readonly StyledProperty<TileMark> MarkProperty = AvaloniaProperty.Register<TileMarkView, TileMark>(nameof(Mark));

    static TileMarkView()
    {
        AffectsRender<TileMarkView>(DotProperty, MarkProperty);
        AffectsMeasure<TileMarkView>(MarkProperty);
    }

    public RunDot Dot { get => GetValue(DotProperty); set => SetValue(DotProperty, value); }
    public TileMark Mark { get => GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

    private static Geometry? Icon(string key) =>
        Avalonia.Application.Current is { } app && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, "Icon." + key, out var g) ? g as Geometry : null;

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => Mark == TileMark.None ? new(7, 7) : new(16, 16);

    public override void Render(DrawingContext ctx)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (Mark == TileMark.None)
        {
            var b = Dot switch { RunDot.Run => Palette.GDotBrush, RunDot.Err => Palette.RBrush, _ => Palette.NDotBrush };
            ctx.DrawEllipse(b, null, c, 3.5, 3.5);
            return;
        }
        if (Icon(Mark == TileMark.Err ? "f-err-f" : "f-warn-f") is not { } g) return;
        double s = 16.0 / 20;
        using var _ = ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(c.X - 8, c.Y - 8));
        ctx.DrawGeometry(Mark == TileMark.Err ? Palette.RBtnBrush : Palette.AmbBrush, null, g);
    }
}
