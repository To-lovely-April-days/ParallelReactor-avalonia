using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 按相对横坐标摆子元素的面板（时间轴刻度、「现在」标签；样稿 left: x% + translateX(-50%)）：子元素的中心在 <see cref="XProperty"/> × 宽度处，
/// 纵向按子元素自己的 VerticalAlignment + Margin（刻度 Top 11、「现在」Bottom 4）。不裁剪子元素（由外层决定），自身宽度随父元素。
/// </summary>
public sealed class RelXPanel : Panel
{
    /// <summary>相对位置 0–1（中心点）。</summary>
    public static readonly AttachedProperty<double> XProperty = AvaloniaProperty.RegisterAttached<RelXPanel, Avalonia.Controls.Control, double>("X");

    public static double GetX(Avalonia.Controls.Control c) => c.GetValue(XProperty);
    public static void SetX(Avalonia.Controls.Control c, double v) => c.SetValue(XProperty, v);

    static RelXPanel()
    {
        AffectsParentArrange<RelXPanel>(XProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? h : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var c in Children)
        {
            var d = c.DesiredSize;
            double x = GetX(c) * finalSize.Width - d.Width / 2;
            double y = c.VerticalAlignment switch
            {
                VerticalAlignment.Bottom => finalSize.Height - d.Height,
                VerticalAlignment.Center => (finalSize.Height - d.Height) / 2,
                _ => 0,
            };
            c.Arrange(new Rect(x, y, d.Width, d.Height));
        }
        return finalSize;
    }
}
