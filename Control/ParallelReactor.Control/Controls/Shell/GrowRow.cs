using System;
using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 一行 flex，子元素 <c>flex: 1 1 auto</c>（样稿 .a2seg span）：先按自然宽度排，剩下的宽度平均分给每个子元素；放不下时按自然宽度比例缩。
/// 子元素拉满行高。<see cref="Spacing"/> = CSS gap。
/// </summary>
public sealed class GrowRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<GrowRow, double>(nameof(Spacing), 2);

    static GrowRow() => AffectsMeasure<GrowRow>(SpacingProperty);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private double[] _w = Array.Empty<double>();

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = Children.Count;
        double h = 0, sum = 0;
        var nat = new double[n];
        for (int i = 0; i < n; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            nat[i] = Children[i].DesiredSize.Width;
            sum += nat[i];
            h = Math.Max(h, Children[i].DesiredSize.Height);
        }
        double gaps = Spacing * Math.Max(0, n - 1);
        double avail = double.IsFinite(availableSize.Width) ? availableSize.Width - gaps : sum;
        _w = new double[n];
        double extra = avail - sum;
        for (int i = 0; i < n; i++)
            _w[i] = extra >= 0 ? nat[i] + extra / Math.Max(1, n) : sum > 0 ? nat[i] * avail / sum : 0;
        for (int i = 0; i < n; i++) Children[i].Measure(new Size(_w[i], h));
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : sum + gaps, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < Children.Count && i < _w.Length; i++)
        {
            Children[i].Arrange(new Rect(x, 0, _w[i], finalSize.Height));
            x += _w[i] + Spacing;
        }
        return finalSize;
    }
}
