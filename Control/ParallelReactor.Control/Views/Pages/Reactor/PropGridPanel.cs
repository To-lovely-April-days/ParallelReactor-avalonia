using System;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 属性卡片的网格（样稿 .reactor-propgrid：repeat(列数, minmax(0, 1fr))、列间距 14、内边距 上 2 · 右 12 · 左 16，卡片之间没有行距 —— 卡片自己有下边线）。
/// 列数 = <see cref="ReactorVesselVm.ColumnsFor"/>（同视图模型算「放得下几张」用的公式，只看父元素给的宽度，R4）；行高 = 这一行卡片里最高的（83）。
/// </summary>
public sealed class PropGridPanel : Panel
{
    public const double PadTop = 2, PadLeft = 16, PadRight = 12;

    /// <summary>此刻的列数（核对用）。</summary>
    public int Columns { get; private set; } = 2;

    private double ColumnWidth(double width, int cols) =>
        Math.Max(0, (width - PadLeft - PadRight - (cols - 1) * ReactorVesselVm.PropGap) / cols);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 520 : availableSize.Width;
        int cols = ReactorVesselVm.ColumnsFor(width);
        Columns = cols;
        double cw = ColumnWidth(width, cols);
        double h = PadTop, rowH = 0;
        int i = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(new Size(cw, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % cols == 0) { h += rowH; rowH = 0; }
        }
        h += rowH;
        return new Size(width, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = ReactorVesselVm.ColumnsFor(finalSize.Width);
        Columns = cols;
        double cw = ColumnWidth(finalSize.Width, cols);
        double y = PadTop, rowH = 0;
        int i = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            int col = i % cols;
            if (col == 0 && i > 0) { y += rowH; rowH = 0; }
            double x = PadLeft + col * (cw + ReactorVesselVm.PropGap);
            c.Arrange(new Rect(x, y, cw, c.DesiredSize.Height));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            i++;
        }
        return finalSize;
    }
}
