using System;
using Avalonia;
using Avalonia.Media;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Vessel;

/// <summary>
/// 反应釜小图标（样稿 ui.stateGlyph / ui.vesselGlyph：16 网格的中性线描，状态只体现在液体深浅上；列表里靠点 + 字）。
/// 默认 16 px；检查器对象标题 30 px，陈列页 26 px，反应釜页的切换按钮 22 px（M5）。线宽随尺寸缩放（同样稿的内联 SVG，没有 non-scaling-stroke）：
/// 轮廓 1.35（圆线帽 / 圆拐角）、液体描边 0.8（单位都是 16 网格）。
/// 轮廓：未安装 line 虚线 2 / 1.6（没有液体）、报警 v-line-alarm、离线 v-detail、其余 v-line；
/// 液体：运行 v-liq-run、报警 v-liq-alarm、其余 v-liq，离线灰斜纹（#pr-hatch：bg-sunken 底 + line-soft 条纹，16 网格里 6 单位一格、条纹宽 1.2、45°）；
/// 液体描边 = 状态的液面线色（离线 v-detail）。颜色全部来自 StateStyles / Palette。
/// （M2 的外壳先用的是 Controls/Pr 里的临时版本，斜纹是近似画的；M4 换成这个。）
/// </summary>
public sealed class VesselGlyph : Avalonia.Controls.Control
{
    public static readonly StyledProperty<StateKey> KeyProperty = AvaloniaProperty.Register<VesselGlyph, StateKey>(nameof(Key));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<VesselGlyph, double>(nameof(Size), 16);

    static VesselGlyph()
    {
        AffectsRender<VesselGlyph>(KeyProperty, SizeProperty);
        AffectsMeasure<VesselGlyph>(SizeProperty);
    }

    /// <summary>状态键（ReactorVm.Key / 检查器的 Key）。</summary>
    public StateKey Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    /// <summary>边长（px），默认 16。</summary>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    // 16 网格的几何（样稿 stateGlyph 的 path，原样）
    private static readonly Geometry Liquid = Geometry.Parse("M4.6 8.6q3.4 1.3 6.8 0v1.5a3.4 3.4 0 0 1-6.8 0z");
    private static readonly Geometry Outline = Geometry.Parse("M2.5 4.5h11M4 4.5v5.6a4 4 0 0 0 8 0V4.5M8 1.2v3.3");

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        double size = Size;
        if (!(size > 0)) return;
        var st = StateStyles.Get(Key);
        bool un = st.Kind == StateKind.Uninstalled, off = st.Kind == StateKind.Offline;
        double s = size / 16.0;
        double ox = (Bounds.Width - size) / 2, oy = (Bounds.Height - size) / 2;
        using var _ = context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy));
        if (!un)
        {
            var edge = VesselPaint.Pen(off ? Palette.VDetailColor : st.LiquidLine?.Color ?? Palette.VDetailColor, 0.8);
            if (off)
            {
                context.DrawGeometry(VesselPaint.Brush(Palette.BgSunkenColor), null, Liquid);
                using (context.PushGeometryClip(Liquid))
                {
                    // #pr-hatch：条纹中心在 x + y = √2 · (6n + 0.6) 上，宽 1.2（16 网格单位）
                    var pen = VesselPaint.Pen(Palette.LineSoftColor, 1.2);
                    double step = 6 * Math.Sqrt(2), o = 0.6 * Math.Sqrt(2);
                    for (int n = 0; n <= 4; n++)
                    {
                        double c = n * step + o;
                        context.DrawLine(pen, new Point(c - 16, 16), new Point(c, 0));
                    }
                }
                context.DrawGeometry(null, edge, Liquid);
            }
            else context.DrawGeometry(st.Liquid, edge, Liquid);
        }
        // 轮廓 1.35，圆线帽 / 圆拐角；未安装虚线 2 / 1.6
        var outline = VesselPaint.Pen(st.VesselLine.Color, 1.35, un ? "2 1.6" : null, PenLineCap.Round, PenLineJoin.Round);
        context.DrawGeometry(null, outline, Outline);
    }
}
