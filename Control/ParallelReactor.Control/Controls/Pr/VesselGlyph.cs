using System;
using Avalonia;
using Avalonia.Media;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 反应釜小图标（样稿 ui.stateGlyph / ui.vesselGlyph：16 网格的中性线描，状态只体现在液体深浅上）。检查器对象标题里 30 px，图例 16 px。
/// 线宽随尺寸缩放（同样稿的内联 SVG：轮廓 1.35、液面 0.8，单位是 16 网格）。轮廓：未安装 line 虚线（2 / 1.6）、报警 v-line-alarm、
/// 离线 v-detail、其余 v-line；液体：运行 v-liq-run、报警 v-liq-alarm、离线灰斜纹、其余 v-liq（颜色全部来自 StateStyles / Palette）。
/// </summary>
public sealed class VesselGlyph : Avalonia.Controls.Control
{
    public static readonly StyledProperty<StateKey> KeyProperty = AvaloniaProperty.Register<VesselGlyph, StateKey>(nameof(Key));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<VesselGlyph, double>(nameof(Size), 16);

    static VesselGlyph()
    {
        AffectsRender<VesselGlyph>(KeyProperty);
        AffectsMeasure<VesselGlyph>(SizeProperty);
    }

    public StateKey Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    // 16 网格的几何（样稿 stateGlyph 的 path，原样）
    private static readonly Geometry Liquid = Geometry.Parse("M4.6 8.6q3.4 1.3 6.8 0v1.5a3.4 3.4 0 0 1-6.8 0z");
    private static readonly Geometry Outline = Geometry.Parse("M2.5 4.5h11 M4 4.5v5.6a4 4 0 0 0 8 0V4.5 M8 1.2v3.3");

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var st = StateStyles.Get(Key);
        double s = Size / 16.0;
        double ox = (Bounds.Width - Size) / 2, oy = (Bounds.Height - Size) / 2;
        using var _ = context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy));
        bool un = st.Kind == StateKind.Uninstalled, off = st.Kind == StateKind.Offline;
        if (!un)
        {
            if (off)
            {
                // 离线：灰色斜纹（bg-sunken 底 + line-soft 斜线，同 #pr-hatch），液面线 v-detail
                context.DrawGeometry(Palette.BgSunkenBrush, null, Liquid);
                using (context.PushGeometryClip(Liquid))
                {
                    var pen = Palette.Pen(StateStyles.Hatch.Line, 0.8);
                    for (double x = 0; x < 20; x += 2.2) context.DrawLine(pen, new Point(x, 12), new Point(x + 4, 8));
                }
                context.DrawGeometry(null, Palette.Pen(Palette.VDetailColor, 0.8), Liquid);
            }
            else
            {
                context.DrawGeometry(st.Liquid, st.LiquidLine is { } ll ? Palette.Pen(ll.Color, 0.8) : null, Liquid);
            }
        }
        var line = st.VesselLine.Color;
        // 虚线 2 / 1.6（16 网格单位；Avalonia 的虚线以线宽为单位）
        var outline = un ? Palette.Pen(line, 1.35, "1.481 1.185", PenLineCap.Round) : Palette.Pen(line, 1.35, cap: PenLineCap.Round);
        context.DrawGeometry(null, outline, Outline);
    }
}
