using System;
using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 只有一个子元素的容器：测量时把父元素给的宽度报出来（<see cref="WidthChanged"/>，变了才报），再量子元素 ——
/// 「程序」画布按滚动区的外宽排卡片（样稿 cv.offsetWidth），这样同一遍排版里就用上新宽度，不用等 SizeChanged 再排一遍（R4：只看父元素给的尺寸）。
/// </summary>
public sealed class WidthProbe : Decorator
{
    private double _last = -1;

    /// <summary>父元素给的宽度（变了才触发；在测量子元素之前、排版的过程中）。</summary>
    public event Action<double>? WidthChanged;

    /// <summary>最近一次测量时的宽度（还没量过为 −1）。</summary>
    public double LastWidth => _last;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = availableSize.Width;
        if (!double.IsInfinity(w) && Math.Abs(w - _last) > 0.01)
        {
            _last = w;
            WidthChanged?.Invoke(w);
        }
        return base.MeasureOverride(availableSize);
    }
}
