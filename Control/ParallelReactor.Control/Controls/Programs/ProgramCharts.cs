using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Pages.Programs;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Programs;

/// <summary>自绘图表的公共部分：13 px 字（UiFont）、按 SVG 的基线对齐、把字报给截图审计。</summary>
public abstract class ProgramChartBase : Avalonia.Controls.Control, IAuditText
{
    protected const double Fs = 13;
    private readonly List<AuditTextRun> _audit = new();

    public IEnumerable<AuditTextRun> AuditTexts => _audit;

    static ProgramChartBase() => ClipToBoundsProperty.OverrideDefaultValue<ProgramChartBase>(true);

    // 改了压力单位（设置 → 常规）时重画：在界面上时订阅，离开时退订（静态事件不拽住控件）
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PressureDisplay.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PressureDisplay.Changed -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    protected void BeginText() => _audit.Clear();

    /// <summary>画一段字：x 按 anchor（-1 左 / 0 中 / 1 右）对齐，baseline = SVG 的 y。</summary>
    protected void Text(DrawingContext ctx, string s, double x, double baseline, int anchor, Color c, FontWeight w = FontWeight.Normal)
    {
        var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, w), Fs,
            new ImmutableSolidColorBrush(c));
        double x0 = anchor < 0 ? x : anchor == 0 ? x - t.Width / 2 : x - t.Width;
        double y0 = baseline - t.Baseline;
        ctx.DrawText(t, new Point(x0, y0));
        _audit.Add(new AuditTextRun(s, Fs, new Rect(x0, y0, t.Width, t.Height), c));
    }

    protected static StreamGeometry Line(IEnumerable<Point> pts)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        bool first = true;
        foreach (var p in pts)
        {
            if (first) { c.BeginFigure(p, false); first = false; } else c.LineTo(p);
        }
        if (!first) c.EndFigure(false);
        return g;
    }
}

/// <summary>
/// 单路程序的预计温度 / 压力（样稿 pgPreview）：高 ≥ 150 时上下两张（温度、压力，不用双轴），否则只画温度。
/// 左 50 右 14、两张之间 10、每张上 18 下 22；刻度 0 / 一半 / 上限（温度按 50 取整、压力按 0.5 MPa / 5 bar / 50 psi，单位按设置）；每步一条竖线、步号写在温度图上；选中的一步 a-tint 底；
/// 温度 ch-t、压力 ch-p，2 px；右下「共 2 h 01 min」。
/// </summary>
public sealed class PlanPreviewChart : ProgramChartBase
{
    public static readonly StyledProperty<ProgramPlanResult?> PlanProperty = AvaloniaProperty.Register<PlanPreviewChart, ProgramPlanResult?>(nameof(Plan));
    public static readonly StyledProperty<int> SelectedProperty = AvaloniaProperty.Register<PlanPreviewChart, int>(nameof(Selected));

    public ProgramPlanResult? Plan { get => GetValue(PlanProperty); set => SetValue(PlanProperty, value); }
    public int Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    static PlanPreviewChart() => AffectsRender<PlanPreviewChart>(PlanProperty, SelectedProperty);

    public override void Render(DrawingContext ctx)
    {
        BeginText();
        var p = Plan;
        double W = Bounds.Width, H = Bounds.Height;
        if (p is null || W < 200 || H < 60) return;
        bool two = H >= 150;
        const double L = 50, R = 14, gap = 10;
        double rh = (H - (two ? gap : 0) - 22) / (two ? 2 : 1);
        double X(double m) => L + m / Math.Max(1, p.Total) * (W - L - R);
        var rows = two ? new[] { (K: 'T', Lab: "预计温度 (°C)"), (K: 'P', Lab: PressureDisplay.LabelParen("预计压力")) } : new[] { (K: 'T', Lab: "预计温度 (°C)") };
        // 压力（计划里是 MPa）按设置的单位：上限取整的单位 0.5 MPa = 5 bar ≈ 50 psi；刻度 MPa / bar 1 位小数、psi 整数
        var pu = PressureDisplay.Primary;
        double pk = PressureUnits.PerMpa(pu), pStep = pu == PressureUnit.Psi ? 50 : pu == PressureUnit.Bar ? 5 : 0.5;
        int pDec = pu == PressureUnit.Psi ? 0 : 1;
        for (int ri = 0; ri < rows.Length; ri++)
        {
            var (k, lab) = rows[ri];
            double t0 = ri * (rh + gap) + 18, ih = rh - 16;
            double hi = k == 'T'
                ? Math.Ceiling(Math.Max(50, p.Points.Max(q => q.T)) / 50) * 50
                : pu == PressureUnit.MPa ? Math.Max(1, Math.Ceiling(p.Points.Max(q => q.P) * 2) / 2)
                : Math.Max(Math.Ceiling(pk / pStep) * pStep, Math.Ceiling(p.Points.Max(q => q.P) * pk / pStep - 1e-9) * pStep);
            double Y(double v) => t0 + ih - v / hi * ih;
            Text(ctx, lab, L, t0 - 5, -1, Palette.T2Color, FontWeight.SemiBold);
            foreach (var v in new[] { 0, hi / 2, hi })
            {
                ctx.DrawLine(Palette.Pen(Palette.LineFaintColor, 1), new Point(L, Y(v)), new Point(W - R, Y(v)));
                Text(ctx, k == 'P' ? JsNumber.ToFixed(v, pDec) : JsNumber.ToFixed(v, 0), L - 6, Y(v) + 4, 1, Palette.T3Color);
            }
            // 步号：v0.8 有不占时间的步骤（搅拌、结束）和很短的步骤（置换、放空），挨得太近的步号不写（离上一个写出的不到 16 px），免得叠在一起；
            // 选中的那一步一定写
            double lastX = double.NegativeInfinity;
            foreach (var b in p.Bounds)
            {
                if (b.I == Selected) ctx.FillRectangle(Palette.ATintBrush, new Rect(X(b.M0), t0, Math.Max(2, X(b.M1) - X(b.M0)), ih));
                ctx.DrawLine(Palette.Pen(Palette.LineSoftColor, 1), new Point(X(b.M0), t0), new Point(X(b.M0), t0 + ih));
                double cx = (X(b.M0) + X(b.M1)) / 2;
                if (ri == 0 && (cx - lastX >= 16 || b.I == Selected))
                {
                    Text(ctx, (b.I + 1).ToString(CultureInfo.InvariantCulture), cx, t0 + 14, 0, b.I == Selected ? Palette.AColor : Palette.T3Color);
                    lastX = cx;
                }
            }
            var pen = Palette.Pen(k == 'T' ? Palette.ChTColor : Palette.ChPColor, 2);
            ctx.DrawGeometry(null, pen, Line(p.Points.Select(q => new Point(X(q.M), Y(k == 'T' ? q.T : q.P * pk)))));
            ctx.DrawLine(Palette.Pen(Palette.Line2Color, 1), new Point(L, t0 + ih), new Point(W - R, t0 + ih));
        }
        Text(ctx, "共 " + ProgramPlanner.Hm(p.Total), W - R, H - 2, 1, Palette.T3Color);
    }
}

/// <summary>
/// 条件矩阵的各路预计温度（样稿 mxChart）：左 36 右 10 上 8 下 22；横轴每 15 / 30 / 60 min 一格（看总长），纵轴 0 / 一半 / 上限（按 50 取整）；
/// 每路一条线（识别色、1.6 px；RV9–16 虚线），升压排队已经算进去。没有参加的通道时写一句。
/// </summary>
public sealed class MxTempChart : ProgramChartBase
{
    public static readonly StyledProperty<MxChartData?> DataProperty = AvaloniaProperty.Register<MxTempChart, MxChartData?>(nameof(Data));

    public MxChartData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    static MxTempChart() => AffectsRender<MxTempChart>(DataProperty);

    public override void Render(DrawingContext ctx)
    {
        BeginText();
        double W = Bounds.Width, H = Bounds.Height;
        var d = Data;
        if (W < 160 || H < 80) return;
        if (d is null || d.Series.Count == 0)
        {
            Text(ctx, "没有参加的通道", W / 2, H / 2 + 4, 0, Palette.T3Color);
            return;
        }
        const double L = 36, R = 10, T = 8, B = 22;
        double iw = W - L - R, ih = H - T - B;
        double tot = Math.Max(1, d.Series.Max(s => s.Plan.Total));
        double hi = Math.Ceiling(Math.Max(50, d.Series.Max(s => s.Plan.Points.Max(q => q.T))) / 50) * 50;
        double X(double m) => L + m / tot * iw;
        double Y(double v) => T + ih - v / hi * ih;
        int tick = tot > 240 ? 60 : tot > 120 ? 30 : 15;
        for (int m = 0; m <= tot + 1e-9; m += tick)
        {
            ctx.DrawLine(Palette.Pen(Palette.ChGridColor, 1), new Point(X(m), T), new Point(X(m), T + ih));
            Text(ctx, m.ToString(CultureInfo.InvariantCulture), X(m), H - 5, m == 0 ? -1 : 0, Palette.ChTickColor);
        }
        foreach (var v in new[] { 0, hi / 2, hi })
        {
            ctx.DrawLine(Palette.Pen(v > 0 ? Palette.ChGridColor : Palette.ChAxisColor, 1), new Point(L, Y(v)), new Point(L + iw, Y(v)));
            Text(ctx, JsNumber.ToString(v), L - 6, Y(v) + 4, 1, Palette.ChTickColor);
        }
        foreach (var s in d.Series)
            ctx.DrawGeometry(null, Palette.Pen(s.Color, 1.6, s.Dashed ? "5 3" : null), Line(s.Plan.Points.Select(q => new Point(X(q.M), Y(q.T)))));
    }
}

/// <summary>共用参数行的斜纹底（样稿 tr.shr td.sv：135° a-wash / 白 各 4 px）。是个面板：里面的控件画在斜纹上面（审计不把底纹当成被文字压住的控件）。</summary>
public sealed class Hatch : Avalonia.Controls.Decorator
{
    public static readonly StyledProperty<IBrush?> StripeProperty = AvaloniaProperty.Register<Hatch, IBrush?>(nameof(Stripe));
    public static readonly StyledProperty<double> PeriodProperty = AvaloniaProperty.Register<Hatch, double>(nameof(Period), 8);

    public IBrush? Stripe { get => GetValue(StripeProperty); set => SetValue(StripeProperty, value); }
    public double Period { get => GetValue(PeriodProperty); set => SetValue(PeriodProperty, value); }

    static Hatch()
    {
        AffectsRender<Hatch>(StripeProperty, PeriodProperty);
        ClipToBoundsProperty.OverrideDefaultValue<Hatch>(true);
    }

    public override void Render(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height, p = Math.Max(2, Period);
        ctx.FillRectangle(Palette.BgCardBrush, new Rect(0, 0, W, H));
        if (Stripe is null) return;
        // CSS 135° 渐变：沿右下方向每 p 一个周期（条纹宽 p/2），条纹本身是「/」方向；横向间距 = p · √2
        var pen = new ImmutablePen(Stripe.ToImmutable(), p / 2);
        double dx = p * Math.Sqrt(2);
        for (double x = 0; x < W + H; x += dx)
            ctx.DrawLine(pen, new Point(x - H, H + 1), new Point(x + 1, -1));
    }
}
