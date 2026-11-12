using System;
using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>
/// 陈列页的等宽多列网格（CSS <c>grid-template-columns: repeat(N, minmax(0, 1fr)); gap: 行 列; align-items: start</c>）：
/// 子元素按顺序逐行填入，每行高度 = 这一行最高的子元素（不像 UniformGrid 那样所有行等高）。
/// </summary>
public sealed class GalleryGrid : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<GalleryGrid, int>(nameof(Columns), 3);
    public static readonly StyledProperty<double> ColumnGapProperty = AvaloniaProperty.Register<GalleryGrid, double>(nameof(ColumnGap), 24);
    public static readonly StyledProperty<double> RowGapProperty = AvaloniaProperty.Register<GalleryGrid, double>(nameof(RowGap), 16);

    static GalleryGrid() => AffectsMeasure<GalleryGrid>(ColumnsProperty, ColumnGapProperty, RowGapProperty);

    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double ColumnGap { get => GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
    public double RowGap { get => GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

    private double ColumnWidth(double width)
    {
        int n = Math.Max(1, Columns);
        return double.IsInfinity(width) ? double.PositiveInfinity : Math.Max(0, (width - (n - 1) * ColumnGap) / n);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = Math.Max(1, Columns);
        double colW = ColumnWidth(availableSize.Width), maxW = 0, total = 0, rowH = 0;
        int i = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(colW, double.PositiveInfinity));
            maxW = Math.Max(maxW, c.DesiredSize.Width);
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % n == 0) { total += rowH + RowGap; rowH = 0; }
        }
        if (i % n != 0) total += rowH; else if (i > 0) total -= RowGap;
        double w = double.IsInfinity(colW) ? n * maxW + (n - 1) * ColumnGap : availableSize.Width;
        return new Size(w, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int n = Math.Max(1, Columns);
        double colW = ColumnWidth(finalSize.Width), y = 0, rowH = 0;
        int i = 0;
        foreach (var c in Children)
        {
            int col = i % n;
            // 列的左右边按整像素取（同网页把格子对齐到像素）：否则每列的宽度各自向上取整，最后一列会超出 1–2 px、被外层裁掉右边框
            var x0 = Math.Round(col * (colW + ColumnGap));
            var x1 = Math.Round(col * (colW + ColumnGap) + colW);
            c.Arrange(new Rect(x0, y, Math.Max(0, x1 - x0), c.DesiredSize.Height));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % n == 0) { y += rowH + RowGap; rowH = 0; }
        }
        return finalSize;
    }
}
