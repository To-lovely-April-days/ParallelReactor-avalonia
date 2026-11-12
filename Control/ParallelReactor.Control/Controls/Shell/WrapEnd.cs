using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 换行的一行（CSS <c>display: flex; flex-wrap: wrap; align-items: center</c>，最后一项 <c>margin-left: auto</c>）：子元素按自然宽度从左排，
/// 放不下就换到下一行；<b>最后一个子元素</b>靠它所在那一行的右端（样稿检查器的「不是当前页签」提示条：图标 · 说明 … 「回到 1号机 · RV1」）。
/// 单个子元素比一整行还宽时按整行宽度排（文字自己折行）。行内纵向居中；<see cref="RowGap"/> = 行间距。左右间距用子元素的 Margin。
/// </summary>
public sealed class WrapEnd : Panel
{
    public static readonly StyledProperty<double> RowGapProperty = AvaloniaProperty.Register<WrapEnd, double>(nameof(RowGap), 2);

    static WrapEnd() => AffectsMeasure<WrapEnd>(RowGapProperty);

    public double RowGap { get => GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowH = 0, maxW = 0;
        int rowStart = 0;
        var kids = Children;
        int last = kids.Count - 1;
        // 先量（不限宽，超出一行时按一行的宽度再量一次：文字折行）
        if (!arrange)
            foreach (var c in kids)
            {
                c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (!double.IsInfinity(width) && c.DesiredSize.Width > width) c.Measure(new Size(width, double.PositiveInfinity));
            }

        void FinishRow(int end)
        {
            if (!arrange) return;
            double cx = 0;
            for (int i = rowStart; i < end; i++)
            {
                var c = kids[i];
                if (!c.IsVisible) continue;
                var w = Math.Min(c.DesiredSize.Width, width);
                if (i == last && !double.IsInfinity(width)) cx = Math.Max(cx, width - w);
                c.Arrange(new Rect(cx, y + (rowH - c.DesiredSize.Height) / 2, w, c.DesiredSize.Height));
                cx += w;
            }
        }

        for (int i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (!c.IsVisible) continue;
            var w = Math.Min(c.DesiredSize.Width, double.IsInfinity(width) ? double.MaxValue : width);
            if (x > 0 && x + w > width + 0.5)
            {
                FinishRow(i);
                y += rowH + RowGap;
                x = 0;
                rowH = 0;
                rowStart = i;
            }
            x += w;
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            maxW = Math.Max(maxW, x);
        }
        FinishRow(kids.Count);
        return new Size(double.IsInfinity(width) ? maxW : Math.Min(maxW, width), y + rowH);
    }
}
