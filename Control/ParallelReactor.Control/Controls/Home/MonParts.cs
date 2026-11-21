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

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => Mark == TileMark.None ? new(7, 7) : new(16, 16);

    public override void Render(DrawingContext ctx)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (Mark == TileMark.None)
        {
            var b = Dot switch { RunDot.Run => Palette.GDotBrush, RunDot.Err => Palette.RBrush, RunDot.Brand => Palette.ABrush, RunDot.Amb => Palette.AxAmbBrush, _ => Palette.NDotBrush };
            ctx.DrawEllipse(b, null, c, 3.5, 3.5);
            return;
        }
        IconView.Draw(ctx, Mark == TileMark.Err ? "err-f" : "warn-f", 16, Mark == TileMark.Err ? Palette.RBtnBrush : Palette.AmbBrush, c.X - 8, c.Y - 8);
    }
}
