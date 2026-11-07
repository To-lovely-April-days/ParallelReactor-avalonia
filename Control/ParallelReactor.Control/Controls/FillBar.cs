using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls;

/// <summary>mock .progress-bar span：按 Percent（0–100）填充左侧，渐变 #efffe4→#d9fbb9，右边 1px #e1f3d2。放在 Border.progress-bar 内。</summary>
public sealed class FillBar : Avalonia.Controls.Control
{
    public static readonly StyledProperty<double> PercentProperty = AvaloniaProperty.Register<FillBar, double>(nameof(Percent));
    public double Percent { get => GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    private static readonly IBrush Fill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#efffe4"), 0), new GradientStop(Color.Parse("#d9fbb9"), 1) },
    };
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.Parse("#e1f3d2")), 1);

    static FillBar() => AffectsRender<FillBar>(PercentProperty);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width * System.Math.Clamp(Percent, 0, 100) / 100.0;
        if (w <= 0) return;
        ctx.FillRectangle(Fill, new Rect(0, 0, w, Bounds.Height));
        ctx.DrawLine(Edge, new Point(w - 0.5, 0), new Point(w - 0.5, Bounds.Height));
    }
}
