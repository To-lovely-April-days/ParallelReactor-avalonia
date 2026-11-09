using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 徽标里的一行（点 / 图标 + 字；样稿 .pr-badge 的 inline-flex，字可以省略）：横排的 StackPanel，但放不下时<b>最后一个</b>可见的子元素（字）按剩下的宽度重新量，
/// 字带 CharacterEllipsis 时就显示「其他客户端 PC-…」而不是画出框外（StackPanel 横排时总按无限宽量子元素，外面的框缩了字也不缩）。
/// 放得下时与 StackPanel 完全一样；样式照旧按 StackPanel 匹配（StyleKeyOverride）。
/// </summary>
public sealed class BadgeRow : StackPanel
{
    protected override Type StyleKeyOverride => typeof(StackPanel);

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (Orientation != Orientation.Horizontal || double.IsInfinity(availableSize.Width) || size.Width <= availableSize.Width + 0.01) return size;
        var visible = Children.Where(c => c.IsVisible).ToList();
        if (visible.Count == 0) return size;
        var last = visible[^1];
        double others = 0;
        for (int i = 0; i < visible.Count - 1; i++) others += visible[i].DesiredSize.Width;
        others += Spacing * (visible.Count - 1);
        last.Measure(new Size(Math.Max(0, availableSize.Width - others), availableSize.Height));
        return new Size(others + last.DesiredSize.Width, size.Height);
    }
}
