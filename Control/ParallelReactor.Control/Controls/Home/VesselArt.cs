using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Home;

/// <summary>
/// 主页「气路图」「卡片」两种视图里的插画式反应釜（融合版 F4，样稿 vesselSVG：viewBox 96 × 124，釜身中心 x = 40，右侧 x 80–87 是温度计）。
/// <list type="bullet">
/// <item>加热套：加热中橙色渐变（越浓功率越大：不透明度 0.45 + 0.55 × 加热 %）、降温中浅蓝、其余浅灰；</item>
/// <item>玻璃釜身 + 液体（停用不画）+ 搅拌轴和桨；正在进气时三个气泡；</item>
/// <item>压力表：灰色整圈轨道 + 品牌蓝当前压力弧（报警红）+ 红色报警上限刻线；</item>
/// <item>温度计：液柱 = 当前温度、黑刻线 + 小三角 = 目标；报警时釜身轮廓变红；停用整只釜 45 % 不透明。</item>
/// </list>
/// 线宽随图缩放（同样稿的内联 SVG）。宽度由版面给（气路图 64 / 88 / 44 / 60，卡片 96 / 116 / 66 / 64），高度 = 宽 × 124 / 96。
/// 颜色只取色板：样稿的几支渐变（钢、电机、玻璃、加热套、温度计）没有同名令牌，用最接近的令牌拼（交接里列了缺的令牌）。
/// 截图冻结，样稿的桨 / 气泡动画这里不做（静止画面同样稿 .freeze）。
/// </summary>
public sealed class VesselArt : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> HeatProperty = AvaloniaProperty.Register<VesselArt, double>(nameof(Heat));
    public static readonly StyledProperty<bool> CoolingProperty = AvaloniaProperty.Register<VesselArt, bool>(nameof(Cooling));
    public static readonly StyledProperty<bool> OffProperty = AvaloniaProperty.Register<VesselArt, bool>(nameof(Off));
    public static readonly StyledProperty<bool> ErrProperty = AvaloniaProperty.Register<VesselArt, bool>(nameof(Err));
    public static readonly StyledProperty<bool> BubblesProperty = AvaloniaProperty.Register<VesselArt, bool>(nameof(Bubbles));
    public static readonly StyledProperty<double> TFracProperty = AvaloniaProperty.Register<VesselArt, double>(nameof(TFrac), double.NaN);
    public static readonly StyledProperty<double> SpFracProperty = AvaloniaProperty.Register<VesselArt, double>(nameof(SpFrac), double.NaN);
    public static readonly StyledProperty<double> PFracProperty = AvaloniaProperty.Register<VesselArt, double>(nameof(PFrac));
    public static readonly StyledProperty<double> LimFracProperty = AvaloniaProperty.Register<VesselArt, double>(nameof(LimFrac), double.NaN);

    static VesselArt()
    {
        AffectsRender<VesselArt>(HeatProperty, CoolingProperty, OffProperty, ErrProperty, BubblesProperty, TFracProperty, SpFracProperty, PFracProperty, LimFracProperty);
    }

    /// <summary>加热功率 %（&gt; 0 = 加热中：橙色加热套）。</summary>
    public double Heat { get => GetValue(HeatProperty); set => SetValue(HeatProperty, value); }
    /// <summary>降温中：浅蓝加热套。</summary>
    public bool Cooling { get => GetValue(CoolingProperty); set => SetValue(CoolingProperty, value); }
    /// <summary>停用：不画液体，整只釜 45 % 不透明（样稿 .vsl.off 还去色 —— 这里本来就只剩灰色部件）。</summary>
    public bool Off { get => GetValue(OffProperty); set => SetValue(OffProperty, value); }
    /// <summary>报警：釜身轮廓、压力弧变红。</summary>
    public bool Err { get => GetValue(ErrProperty); set => SetValue(ErrProperty, value); }
    /// <summary>正在进气（进气阀开、有气源开着）：三个气泡。</summary>
    public bool Bubbles { get => GetValue(BubblesProperty); set => SetValue(BubblesProperty, value); }
    /// <summary>温度 / 量程（NaN = 没有读数：温度计不画液柱）。</summary>
    public double TFrac { get => GetValue(TFracProperty); set => SetValue(TFracProperty, value); }
    /// <summary>目标温度 / 量程（NaN = 没有目标）。</summary>
    public double SpFrac { get => GetValue(SpFracProperty); set => SetValue(SpFracProperty, value); }
    /// <summary>压力 / 量程（≤ 0.01 不画弧）。</summary>
    public double PFrac { get => GetValue(PFracProperty); set => SetValue(PFracProperty, value); }
    /// <summary>压力报警上限 / 量程（NaN = 不画刻线）。</summary>
    public double LimFrac { get => GetValue(LimFracProperty); set => SetValue(LimFracProperty, value); }

    /// <summary>viewBox 的宽高。</summary>
    public const double VbW = 96, VbH = 124;

    // —— 几何（样稿 vesselSVG 原样） ——
    private static readonly Geometry Jacket = Geometry.Parse("M10 36Q10 32 14 32H66Q70 32 70 36V88A30 30 0 0 1 10 88Z");
    private static readonly Geometry Glass = Geometry.Parse("M15 26V88A25 25 0 0 0 65 88V26Z");
    private static readonly Geometry BodyLine = Geometry.Parse("M15 26V88A25 25 0 0 0 65 88V26");
    private static readonly Rect LiquidRect = new(15, 56, 50, 62);
    private static readonly (double X, double Y, double R)[] Bubs = { (30, 84, 2.2), (48, 76, 1.8), (34, 68, 1.5) };
    private const double GX = 62, GY = 8, GR = 4.6, TY = 32, TH = 84;

    // —— 画刷（样稿 defs 里的渐变；颜色取最接近的令牌） ——
    private static IImmutableBrush HGrad(params (double Off, Color C)[] stops) => Grad(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative), stops);
    private static IImmutableBrush VGrad(params (double Off, Color C)[] stops) => Grad(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative), stops);

    private static IImmutableBrush Grad(RelativePoint a, RelativePoint b, (double Off, Color C)[] stops)
    {
        var list = new List<ImmutableGradientStop>(stops.Length);
        foreach (var (o, c) in stops) list.Add(new ImmutableGradientStop(o, c));
        return new ImmutableLinearGradientBrush(list, 1, null, null, GradientSpreadMethod.Pad, a, b);
    }

    /// <summary>样稿 gSteel（横向三段灰）：v-tb-line → bg-sunken → line-2。</summary>
    private static readonly IImmutableBrush Steel = HGrad((0, Palette.VTbLineColor), (0.45, Palette.BgSunkenColor), (1, Palette.Line2Color));
    /// <summary>样稿 gMotor（竖向）：bg-sunken → v-tb-line。</summary>
    private static readonly IImmutableBrush Motor = VGrad((0, Palette.BgSunkenColor), (1, Palette.VTbLineColor));
    /// <summary>样稿 gGlass：v-tb → 白 → line-soft。</summary>
    private static readonly IImmutableBrush GlassFill = HGrad((0, Palette.VTbColor), (0.35, Palette.BgCardColor), (1, Palette.LineSoftColor));
    /// <summary>gLiq = v-liq → v-liq-2（竖向）。</summary>
    private static readonly IImmutableBrush Liquid = VGrad((0, Palette.VLiqColor), (1, Palette.VLiq2Color));
    /// <summary>样稿 gJH（橙）：两端 ch-t，中间 ch-t 55 %（叠在白底上接近样稿的浅橙）。</summary>
    private static readonly IImmutableBrush JacketHeat = HGrad((0, Palette.ChTColor), (0.5, Palette.WithAlpha(Palette.ChTColor, 0.55)), (1, Palette.ChTColor));
    /// <summary>样稿 gJC（浅蓝）：v-liq-run-line → v-liq → v-liq-run-line。</summary>
    private static readonly IImmutableBrush JacketCool = HGrad((0, Palette.VLiqRunLineColor), (0.5, Palette.VLiqColor), (1, Palette.VLiqRunLineColor));
    /// <summary>样稿 gJI（浅灰）：v-tb-line → v-tb → line-2。</summary>
    private static readonly IImmutableBrush JacketIdle = HGrad((0, Palette.VTbLineColor), (0.5, Palette.VTbColor), (1, Palette.Line2Color));
    /// <summary>样稿 gT（竖向）：ch-t → ch-t 55 %。</summary>
    private static readonly IImmutableBrush Thermo = VGrad((0, Palette.ChTColor), (1, Palette.WithAlpha(Palette.ChTColor, 0.55)));
    private static readonly IImmutableBrush White = new ImmutableSolidColorBrush(Palette.BgCardColor);
    private static readonly IImmutableBrush Highlight = new ImmutableSolidColorBrush(Palette.BgCardColor, 0.75);

    private static ImmutablePen Pen(Color c, double w, PenLineCap cap = PenLineCap.Flat) =>
        new(new ImmutableSolidColorBrush(c), w, null, cap, PenLineJoin.Miter);

    private static readonly ImmutablePen BodyPen = Pen(Palette.VLineColor, 1), BodyErrPen = Pen(Palette.VLineAlarmColor, 1.4);
    private static readonly ImmutablePen SurfPen = Pen(Palette.VLiqRunLineColor, 1);
    private static readonly ImmutablePen ShaftPen = Pen(Palette.VShaftColor, 1.8, PenLineCap.Round);
    private static readonly ImmutablePen BubPen = Pen(Palette.VBubColor, 0.8);
    private static readonly ImmutablePen DetailPen = Pen(Palette.VDetailColor, 0.8);
    private static readonly ImmutablePen TrackPen = Pen(Palette.LineSoftColor, 2.2, PenLineCap.Round);
    private static readonly ImmutablePen ValuePen = Pen(Palette.AColor, 2.2, PenLineCap.Round), ValueErrPen = Pen(Palette.RColor, 2.2, PenLineCap.Round);
    private static readonly ImmutablePen LimPen = Pen(Palette.RColor, 1.3);
    private static readonly ImmutablePen TubePen = Pen(Palette.VTbLineColor, 0.8);
    private static readonly ImmutablePen SpPen = Pen(Palette.VSensorColor, 1.4);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) ? availableSize.Width : VbW;
        double h = w * VbH / VbW;
        if (double.IsFinite(availableSize.Height) && h > availableSize.Height) { h = availableSize.Height; w = h * VbW / VbH; }
        return new Size(w, h);
    }

    /// <summary>压力表上 v（0–1）对应的角度（弧度）：−135° … +135°。</summary>
    public static double Angle(double v) => (-135 + 270 * Math.Clamp(v, 0, 1)) * Math.PI / 180;

    private static Point Xy(double a, double r) => new(GX + r * Math.Sin(a), GY - r * Math.Cos(a));

    private static Geometry Arc(double v0, double v1)
    {
        double a0 = Angle(v0), a1 = Angle(v1);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Xy(a0, GR), false);
            c.ArcTo(Xy(a1, GR), new Size(GR, GR), 0, a1 - a0 > Math.PI, SweepDirection.Clockwise);
            c.EndFigure(false);
        }
        return g;
    }

    private static readonly Geometry FullArc = Arc(0, 1);

    /// <summary>温度计上 v（0–1）对应的 y（viewBox）。</summary>
    public static double ThermoY(double v) => TY + TH - Math.Clamp(v, 0, 1) * TH;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (!(w > 0) || !(h > 0)) return;
        double k = Math.Min(w / VbW, h / VbH);
        double ox = (w - VbW * k) / 2, oy = (h - VbH * k) / 2;
        using var _t = ctx.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(ox, oy));
        bool off = Off, err = Err;
        using var _o = off ? ctx.PushOpacity(0.45) : default(DrawingContext.PushedState?);

        // 加热套
        double heat = off ? 0 : Heat;
        var jk = heat > 0 ? JacketHeat : Cooling && !off ? JacketCool : JacketIdle;
        using (heat > 0 ? ctx.PushOpacity(0.45 + 0.55 * Math.Clamp(heat, 0, 100) / 100) : default(DrawingContext.PushedState?))
            ctx.DrawGeometry(jk, null, Jacket);
        // 玻璃釜身 + 釜内（裁在釜身里）
        ctx.DrawGeometry(GlassFill, null, Glass);
        using (ctx.PushGeometryClip(Glass))
        {
            if (!off)
            {
                ctx.DrawRectangle(Liquid, null, LiquidRect);
                ctx.DrawLine(SurfPen, new Point(15, 56), new Point(65, 56));
            }
            if (Bubbles && !off)
                foreach (var (x, y, r) in Bubs) ctx.DrawEllipse(White, BubPen, new Point(x, y), r, r);
            ctx.DrawLine(ShaftPen, new Point(40, 20), new Point(40, 98));
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Palette.VShaftColor), null, new RoundedRect(new Rect(29, 97.5, 22, 4), 2));
            ctx.DrawRectangle(Highlight, null, new RoundedRect(new Rect(19, 30, 3.5, 72), 1.75));
        }
        ctx.DrawGeometry(null, err ? BodyErrPen : BodyPen, BodyLine);
        // 釜盖、电机、压力表
        ctx.DrawRectangle(Steel, DetailPen, new RoundedRect(new Rect(11, 18, 58, 8), 3));
        var cap = new ImmutableSolidColorBrush(Palette.VCapColor);
        ctx.DrawRectangle(cap, null, new Rect(37, 14.5, 6, 3.5));
        ctx.DrawRectangle(Motor, DetailPen, new RoundedRect(new Rect(30, 3, 20, 12), 3));
        ctx.DrawRectangle(cap, null, new Rect(61.2, 13, 1.6, 5));
        ctx.DrawEllipse(White, DetailPen, new Point(GX, GY), 6.6, 6.6);
        ctx.DrawGeometry(null, TrackPen, FullArc);
        double pv = Math.Clamp(PFrac, 0, 1);
        if (pv > 0.01 && !off) ctx.DrawGeometry(null, err ? ValueErrPen : ValuePen, Arc(0, pv));
        if (double.IsFinite(LimFrac))
        {
            double lim = Angle(LimFrac);
            ctx.DrawLine(LimPen, Xy(lim, 3.1), Xy(lim, 6.3));
        }
        // 温度计
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Palette.VTbColor), TubePen, new RoundedRect(new Rect(80, TY, 7, TH), 3.5));
        if (double.IsFinite(TFrac) && !off)
        {
            double t = ThermoY(TFrac), hh = TY + TH - 0.8 - t;
            if (hh > 0) ctx.DrawRectangle(Thermo, null, new RoundedRect(new Rect(80.8, t, 5.4, hh), Math.Min(2.7, hh / 2)));
        }
        if (double.IsFinite(SpFrac) && !off)
        {
            double sp = ThermoY(SpFrac);
            ctx.DrawLine(SpPen, new Point(78, sp), new Point(89, sp));
            var tri = new StreamGeometry();
            using (var c = tri.Open())
            {
                c.BeginFigure(new Point(74.5, sp - 3), true);
                c.LineTo(new Point(78, sp));
                c.LineTo(new Point(74.5, sp + 3));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(new ImmutableSolidColorBrush(Palette.VSensorColor), null, tri);
        }
    }
}
