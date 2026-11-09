using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>
/// 陈列页的一行（样稿 .gallery-row：<c>display: flex; flex-wrap: wrap; align-items: center; gap: 10px 12px</c>）：
/// 子元素按自然宽度从左排，放不下换行；每行内纵向居中（<see cref="ItemsAlignment"/>：釜体分区 .gallery-vs 是 align-items: flex-end，靠下），
/// 行高 = 这一行最高的子元素。
/// </summary>
public sealed class GalleryRow : Panel
{
    public static readonly StyledProperty<double> ColumnGapProperty = AvaloniaProperty.Register<GalleryRow, double>(nameof(ColumnGap), 12);
    public static readonly StyledProperty<double> RowGapProperty = AvaloniaProperty.Register<GalleryRow, double>(nameof(RowGap), 10);
    public static readonly StyledProperty<VerticalAlignment> ItemsAlignmentProperty =
        AvaloniaProperty.Register<GalleryRow, VerticalAlignment>(nameof(ItemsAlignment), VerticalAlignment.Center);

    static GalleryRow() => AffectsMeasure<GalleryRow>(ColumnGapProperty, RowGapProperty, ItemsAlignmentProperty);

    public double ColumnGap { get => GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
    public double RowGap { get => GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    /// <summary>一行之内子元素的纵向对齐（align-items）：Center（默认）、Bottom（flex-end）、Top（flex-start）。</summary>
    public VerticalAlignment ItemsAlignment { get => GetValue(ItemsAlignmentProperty); set => SetValue(ItemsAlignmentProperty, value); }

    private readonly List<(int From, int To, double Height)> _lines = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        _lines.Clear();
        double x = 0, lineH = 0, total = 0, maxW = 0;
        int from = 0, count = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            c.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            if (!c.IsVisible) continue;
            var w = c.DesiredSize.Width;
            if (count > 0 && x + ColumnGap + w > availableSize.Width + 0.01)
            {
                _lines.Add((from, i, lineH));
                total += lineH + RowGap;
                maxW = Math.Max(maxW, x);
                x = 0; lineH = 0; from = i; count = 0;
            }
            x += (count > 0 ? ColumnGap : 0) + w;
            lineH = Math.Max(lineH, c.DesiredSize.Height);
            count++;
        }
        if (count > 0) { _lines.Add((from, Children.Count, lineH)); total += lineH; maxW = Math.Max(maxW, x); }
        return new Size(double.IsInfinity(availableSize.Width) ? maxW : Math.Min(maxW, availableSize.Width), total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var (from, to, h) in _lines)
        {
            double x = 0;
            bool first = true;
            for (int i = from; i < to; i++)
            {
                var c = Children[i];
                if (!c.IsVisible) continue;
                if (!first) x += ColumnGap;
                first = false;
                var w = c.DesiredSize.Width;
                var dy = ItemsAlignment switch
                {
                    VerticalAlignment.Top => 0,
                    VerticalAlignment.Bottom => h - c.DesiredSize.Height,
                    _ => (h - c.DesiredSize.Height) / 2,
                };
                c.Arrange(new Rect(x, y + dy, w, c.DesiredSize.Height));
                x += w;
            }
            y += h + RowGap;
        }
        return finalSize;
    }
}
