using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls;

/// <summary>
/// mock 的 <c>.icon</c>：24×24 viewBox 的描边几何按 <see cref="Size"/> 缩放绘制（stroke 1.45 随缩放，round cap / join，无填充），
/// 颜色继承父级 Foreground（currentColor）。Size 13 = .icon，10 = .small，8 = .tiny，16 = 侧栏，22 = gauge。
/// </summary>
public sealed class IconView : Avalonia.Controls.Control
{
    public static readonly StyledProperty<Geometry?> GeometryProperty = AvaloniaProperty.Register<IconView, Geometry?>(nameof(Geometry));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Size), 13);
    public static readonly StyledProperty<double> StrokeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Stroke), 1.45);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();
    public static readonly StyledProperty<bool> FillProperty = AvaloniaProperty.Register<IconView, bool>(nameof(Fill));

    static IconView()
    {
        AffectsRender<IconView>(GeometryProperty, ForegroundProperty, StrokeProperty, FillProperty);
        AffectsMeasure<IconView>(SizeProperty);
    }

    public Geometry? Geometry { get => GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>描边宽度（viewBox 单位，1.45；gauge 为 1.7，logo 为 1.8）。</summary>
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>true 时同时填充（mock 的图标全部 fill:none，仅状态点等自绘用）。</summary>
    public bool Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var g = Geometry;
        var brush = Foreground;
        if (g == null || brush == null) return;
        var s = Size / 24.0;
        var ox = (Bounds.Width - Size) / 2;
        var oy = (Bounds.Height - Size) / 2;
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
        {
            var pen = new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(Fill ? brush : null, pen, g);
        }
    }
}
